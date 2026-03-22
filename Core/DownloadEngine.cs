using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Steam;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Core;

public class DownloadEngine : IDisposable
{
    private readonly DownloadSession  _session;
    private readonly SteamSession     _steamSession;
    private readonly CancellationToken _ct;

    private readonly ChunkScheduler       _scheduler;
    private readonly GlobalProgress       _progress;
    private readonly Checkpoint           _checkpoint;
    private readonly CdnManager           _cdnManager;
    private readonly FallbackDownloader   _fallback;
    private readonly List<DownloadWorker> _workers = new();
    private FileAssembler? _assembler;

    public event EventHandler<ProgressEventArgs>? ProgressChanged;

    public DownloadEngine(
        DownloadSession session,
        SteamSession    steamSession,
        CancellationToken ct)
    {
        _session      = session;
        _steamSession = steamSession;
        _ct           = ct;
        _progress     = new GlobalProgress();
        _scheduler    = new ChunkScheduler();
        _assembler    = new FileAssembler(session.OutputDir);
        _cdnManager   = new CdnManager(steamSession);

        _session.SessionKey = LocalDatabase.MakeSessionKey(session.AppId, session.OutputDir);
        _checkpoint = Checkpoint.Load(session.AppId, session.OutputDir);

        int already = LocalDatabase.Instance.GetCompletedChunkCount(_session.SessionKey);
        if (already > 0)
            Logger.Info($"Resuming — {already} chunks already done");

        // Progress is initialized lazily in RunAsync once we know chunk sizes
        // Build fallback with already-cached CDN hosts (may be empty until pre-warm)
        _fallback = new FallbackDownloader(steamSession.GetCachedCdnHosts());

        Logger.Debug($"Engine ready: AppID {session.AppId}  key={_session.SessionKey}");
    }

    public async Task RunAsync()
    {
        try
        {
            int total = _session.MaxDownloads;

            Logger.Info($"Starting download — {total} workers " +
                        $"({PrimaryCount(total)} primary + {SecondaryCount(total)} secondary)");

            // Pre-warm CDN server list so FallbackDownloader has the authenticated hosts
            await _steamSession.GetCdnServersAsync();

            // Rebuild fallback now that we have servers
            var updatedFallback = new FallbackDownloader(_steamSession.GetCachedCdnHosts());

            // Create workers — 70% primary, 30% secondary (at least 1 secondary if >1 total)
            for (int i = 0; i < total; i++)
            {
                var engine = i < PrimaryCount(total)
                    ? DownloadWorker.EngineType.Primary
                    : DownloadWorker.EngineType.Secondary;

                var w = new DownloadWorker(
                    i, engine, _scheduler, _progress, _session,
                    _checkpoint, _ct, _steamSession, _cdnManager,
                    updatedFallback, _assembler!);
                w.ProgressChanged += OnWorkerProgress;
                _workers.Add(w);
            }

            var workerTasks = _workers.Select(w => w.RunAsync()).ToList();
            await ScheduleChunksAsync();
            Logger.Debug("All chunks scheduled — waiting for workers...");
            await Task.WhenAll(workerTasks);

            if (_ct.IsCancellationRequested)
            {
                _checkpoint.Save();
                _session.WasCancelled = true;
                Logger.Info("Paused — progress saved");
            }
            else
            {
                Logger.Debug("Finalizing files...");
                await FinalizeAllFilesAsync();
                if (_session.ValidateChecksums) await VerifyFilesAsync();
                _checkpoint.Clear();
                Logger.Success("Download complete!");
            }
        }
        catch (OperationCanceledException)
        {
            _checkpoint.Save();
            _session.WasCancelled = true;
            Logger.Info("Paused — progress saved");
        }
        catch (Exception ex)
        {
            Logger.Error($"Engine error: {ex.Message}");
            throw;
        }
    }

    // 70% primary workers (rounded up), 30% secondary (at least 1 each when total>1)
    private static int PrimaryCount(int total)
    {
        if (total <= 1) return 1;
        return Math.Max(1, (int)Math.Ceiling(total * 0.7));
    }

    private static int SecondaryCount(int total)
    {
        if (total <= 1) return 0;
        return total - PrimaryCount(total);
    }

    private void OnWorkerProgress(object? sender, ProgressEventArgs e)
    {
        var snap = _progress.GetSnapshot();
        ProgressChanged?.Invoke(this, new ProgressEventArgs
        {
            BytesDownloaded = (long)(snap.DownloadedMB * 1_048_576),
            PercentComplete  = snap.Percent,
            SpeedMBps        = snap.SpeedMBps,
            EtaSeconds       = snap.EtaSeconds,
            WorkerId         = e.WorkerId,
            CurrentFile      = e.CurrentFile
        });
    }

    private async Task ScheduleChunksAsync()
    {
        long alreadyBytes  = 0;
        long pendingBytes  = 0;

        // First pass: tally total + already-done bytes so we can seed the progress bar
        foreach (var depot in _session.Depots)
            foreach (var file in depot.Files)
            {
                if (_session.FileFilters.Count > 0 &&
                    !_session.FileFilters.Any(f => FilterMatcher.Matches(file.FileName, f)))
                    continue;

                foreach (var chunk in file.Chunks)
                {
                    long sz = chunk.CompressedLength;
                    if (_checkpoint.IsChunkComplete(chunk.ChunkIdHex))
                        alreadyBytes += sz;
                    else
                        pendingBytes += sz;
                }
            }

        // Seed the progress tracker with already-completed bytes
        // This ensures resume shows the correct % from the start, not 0%
        _progress.SeedCompleted(alreadyBytes);
        _progress.AddTotal(alreadyBytes + pendingBytes);

        // Second pass: enqueue only the pending chunks
        foreach (var depot in _session.Depots)
        {
            Logger.Debug($"Scheduling depot {depot.DepotId}");
            foreach (var file in depot.Files)
            {
                if (_session.FileFilters.Count > 0 &&
                    !_session.FileFilters.Any(f => FilterMatcher.Matches(file.FileName, f)))
                    continue;

                foreach (var chunk in file.Chunks)
                {
                    if (_ct.IsCancellationRequested) return;
                    if (_checkpoint.IsChunkComplete(chunk.ChunkIdHex)) continue;

                    while (_scheduler.PendingCount > 10_000 && !_ct.IsCancellationRequested)
                        await Task.Delay(50, _ct);

                    _scheduler.Enqueue(new ChunkTask
                        { DepotId = depot.DepotId, Chunk = chunk, File = file,
                          Size = chunk.CompressedLength });
                }
            }
        }
        _scheduler.MarkSchedulingComplete();
    }

    private async Task FinalizeAllFilesAsync()
    {
        if (_assembler == null) return;
        foreach (var depot in _session.Depots)
            foreach (var file in depot.Files)
            {
                try   { await _assembler.FinalizeFileAsync(file); }
                catch (Exception ex) { Logger.Warn($"Finalize {file.FileName}: {ex.Message}"); }
            }
    }

    private async Task VerifyFilesAsync()
    {
        Logger.Info("Verifying files...");
        int total = _session.Depots.Sum(d => d.Files.Count), done = 0;
        foreach (var depot in _session.Depots)
            foreach (var file in depot.Files)
            {
                string path = Path.Combine(_session.OutputDir,
                    file.FileName.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(path))
                { if (new FileInfo(path).Length != (long)file.Size) Logger.Warn($"Size mismatch: {file.FileName}"); }
                else Logger.Warn($"Missing: {file.FileName}");

                if (++done % 500 == 0) Logger.Info($"Verified {done}/{total}");
            }
        Logger.Info($"Verification done: {done}/{total}");
        await Task.CompletedTask;
    }

    public DownloadStatistics GetStatistics()
    {
        var s = _progress.GetSnapshot();
        return new DownloadStatistics
        {
            DownloadedMB    = s.DownloadedMB,
            TotalMB         = s.TotalMB,
            Percent         = s.Percent,
            SpeedMBps       = s.SpeedMBps,
            CompletedChunks = _checkpoint.Count,
            TotalChunks     = _scheduler.TotalScheduled,
            IsCompleted     = _scheduler.IsComplete,
            IsPaused        = _ct.IsCancellationRequested
        };
    }

    public void Dispose()
    {
        _assembler?.Dispose();
        _cdnManager.Dispose();
    }
}

public class ProgressEventArgs : EventArgs
{
    public long    BytesDownloaded  { get; set; }
    public double  PercentComplete   { get; set; }
    public double  SpeedMBps         { get; set; }
    public double  EtaSeconds        { get; set; }
    public int     WorkerId          { get; set; }
    public string? CurrentFile       { get; set; }
}
