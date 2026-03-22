using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Steam;
using LustsDepotDownloaderPro.Utils;
using SteamKit2;
using SteamKit2.CDN;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Download worker — cooperative dual-engine model.
///
/// Workers are typed at creation time:
///   Primary   — uses SteamKit2 CdnManager (authenticated, decryption built-in)
///   Secondary — uses FallbackDownloader (raw HTTP, manual AES+decompress)
///
/// Both worker types pull from the SAME ChunkScheduler queue, so they work
/// together on the same download simultaneously.  Allocation:
///   • 70% of workers → Primary
///   • 30% of workers → Secondary  (at least 1 secondary when >1 worker total)
///
/// If a Primary worker fails a chunk after 3 retries, it puts the chunk back
/// in the queue.  A Secondary worker will pick it up — and vice versa.
/// This means neither engine blocks the other: whichever CDN path is working
/// at the moment keeps making progress.
///
/// There is no adaptive health scoring / state machine — the queue is the
/// coordination mechanism.  Simple, correct, fast.
/// </summary>
public class DownloadWorker
{
    public enum EngineType { Primary, Secondary }

    private readonly int           _id;
    private readonly EngineType    _engine;
    private readonly ChunkScheduler _scheduler;
    private readonly GlobalProgress _progress;
    private readonly DownloadSession _session;
    private readonly Checkpoint    _checkpoint;
    private readonly CancellationToken _ct;
    private readonly SteamSession  _steam;
    private readonly CdnManager    _cdnManager;
    private readonly FallbackDownloader _fallback;
    private readonly FileAssembler _assembler;

    private IReadOnlyCollection<Server>? _servers;

    // Consecutive fail counter — after 3 failures reschedule and keep going
    private int _consecutiveFails;
    private const int MaxConsecutiveFails = 3;

    public event EventHandler<ProgressEventArgs>? ProgressChanged;

    public DownloadWorker(
        int id,
        EngineType engine,
        ChunkScheduler scheduler,
        GlobalProgress progress,
        DownloadSession session,
        Checkpoint checkpoint,
        CancellationToken ct,
        SteamSession steam,
        CdnManager cdnManager,
        FallbackDownloader fallback,
        FileAssembler assembler)
    {
        _id          = id;
        _engine      = engine;
        _scheduler   = scheduler;
        _progress    = progress;
        _session     = session;
        _checkpoint  = checkpoint;
        _ct          = ct;
        _steam       = steam;
        _cdnManager  = cdnManager;
        _fallback    = fallback;
        _assembler   = assembler;
    }

    public async Task RunAsync()
    {
        Logger.Debug($"Worker {_id} [{_engine}] started");
        _servers = await _steam.GetCdnServersAsync();

        try
        {
            while (!_ct.IsCancellationRequested)
            {
                if (!_scheduler.TryDequeue(out var task) || task == null)
                {
                    if (_scheduler.IsComplete) break;
                    await Task.Delay(100, _ct);
                    continue;
                }

                try
                {
                    await ProcessChunkAsync(task);
                    _consecutiveFails = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _consecutiveFails++;
                    Logger.Debug($"Worker {_id} [{_engine}] chunk fail #{_consecutiveFails}: {ex.Message}");

                    if (_consecutiveFails >= MaxConsecutiveFails)
                    {
                        // Put chunk back so the OTHER engine's workers can try it
                        _scheduler.MarkFailed(task);
                        // Brief pause so we don't spin-fail the same chunks repeatedly
                        await Task.Delay(500, _ct);
                    }
                    else
                    {
                        _scheduler.MarkFailed(task);
                    }
                }
            }
        }
        catch (OperationCanceledException) { Logger.Debug($"Worker {_id} cancelled"); }
        catch (Exception ex) { Logger.Error($"Worker {_id} fatal: {ex.Message}"); throw; }
    }

    private async Task ProcessChunkAsync(ChunkTask task)
    {
        var depotInfo = _session.Depots.FirstOrDefault(d => d.DepotId == task.DepotId);
        var depotKey  = depotInfo?.DepotKey;

        byte[] data = _engine == EngineType.Primary
            ? await DownloadViaPrimaryAsync(task, depotKey)
            : await DownloadViaSecondaryAsync(task, depotKey);

        await _assembler.WriteChunkAsync(task.File, task.Chunk, data);
        _progress.ReportProgress(task.Size);
        _checkpoint.MarkChunkComplete(task.Chunk.ChunkIdHex);

        var snap = _progress.GetSnapshot();
        ProgressChanged?.Invoke(this, new ProgressEventArgs
        {
            BytesDownloaded = (long)(snap.DownloadedMB * 1_048_576),
            PercentComplete  = snap.Percent,
            SpeedMBps        = snap.SpeedMBps,
            EtaSeconds       = snap.EtaSeconds,
            WorkerId         = _id,
            CurrentFile      = task.File.FileName
        });
    }

    // ─── Primary: SteamKit2 CDN client via CdnManager ────────────────────────

    private async Task<byte[]> DownloadViaPrimaryAsync(ChunkTask task, byte[]? depotKey)
    {
        var servers = (_servers?.ToList() as IReadOnlyList<Server>)
                      ?? Array.Empty<Server>();
        return await _cdnManager.DownloadChunkAsync(
            _session.AppId, task, depotKey, servers, _ct);
    }

    // ─── Secondary: direct HTTP fallback ─────────────────────────────────────

    private async Task<byte[]> DownloadViaSecondaryAsync(ChunkTask task, byte[]? depotKey) =>
        await _fallback.DownloadChunkAsync(task.DepotId, task.Chunk, depotKey, _ct);
}
