using SteamKit2;
using SteamKit2.CDN;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// CDN manager — wraps SteamSession with a 14-day depot-key disk cache
/// and clean chunk-download helpers used by both worker types.
///
/// Only uses SteamKit2 3.2.0 APIs that actually exist:
///   SteamContent.GetServersForSteamPipe()
///   SteamContent.GetCDNAuthToken(appId, depotId, host)
///   SteamContent.GetManifestRequestCode(...)
///   SteamApps.GetDepotDecryptionKey(depotId, appId)
///   CDN.Client.DownloadDepotChunkAsync(depotId, chunk, server, buffer, depotKey, ...)
/// </summary>
public class CdnManager : IDisposable
{
    private readonly SteamSession _steam;

    // Depot key disk cache — 14 days, like node-steam-user
    private static readonly string KeyCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LustsDepotDownloader", "depot_keys");
    private static readonly TimeSpan KeyCacheMaxAge = TimeSpan.FromDays(14);

    public CdnManager(SteamSession steam)
    {
        _steam = steam;
        try { Directory.CreateDirectory(KeyCacheDir); } catch { }
    }

    // ─── Depot key with 14-day disk cache ────────────────────────────────────

    public async Task<byte[]?> GetDepotKeyAsync(uint appId, uint depotId)
    {
        string path = Path.Combine(KeyCacheDir, $"{appId}_{depotId}.key");

        if (File.Exists(path))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < KeyCacheMaxAge)
            {
                try
                {
                    var cached = await File.ReadAllBytesAsync(path);
                    if (cached.Length == 32)
                    {
                        Logger.Debug($"Depot key {depotId}: disk cache ({age.TotalDays:F1}d)");
                        return cached;
                    }
                }
                catch { }
            }
        }

        var key = await _steam.GetDepotKeyAsync(depotId, appId);
        if (key != null)
        {
            try { await File.WriteAllBytesAsync(path, key); } catch { }
        }
        return key;
    }

    // ─── Chunk download via SteamKit2 CDN client ──────────────────────────────

    public async Task<byte[]> DownloadChunkAsync(
        uint appId, Models.ChunkTask task, byte[]? depotKey,
        IReadOnlyList<Server> servers, CancellationToken ct)
    {
        var chunkInfo = new DepotManifest.ChunkData
        {
            ChunkID            = task.Chunk.ChunkId,
            Checksum           = task.Chunk.Checksum,
            Offset             = task.Chunk.Offset,
            CompressedLength   = task.Chunk.CompressedLength,
            UncompressedLength = task.Chunk.UncompressedLength,
        };

        int bufSize = (int)Math.Max(task.Chunk.UncompressedLength, task.Chunk.CompressedLength);
        if (bufSize <= 0) bufSize = 1_048_576;
        byte[] buffer = new byte[bufSize];

        Exception? lastEx = null;

        foreach (var server in servers.Take(6))
        {
            ct.ThrowIfCancellationRequested();

            // Try without token first (works most of the time)
            try
            {
                int written = await _steam.CdnClient.DownloadDepotChunkAsync(
                    task.DepotId, chunkInfo, server, buffer, depotKey,
                    proxyServer: null, cdnAuthToken: null);
                return written == buffer.Length ? buffer : buffer[..written];
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // 401 — try with auth token
                var token = await _steam.GetCdnAuthTokenAsync(server, appId, task.DepotId);
                if (token == null) { lastEx = ex; continue; }
                try
                {
                    int written = await _steam.CdnClient.DownloadDepotChunkAsync(
                        task.DepotId, chunkInfo, server, buffer, depotKey,
                        proxyServer: null, cdnAuthToken: token);
                    return written == buffer.Length ? buffer : buffer[..written];
                }
                catch (Exception ex2) { lastEx = ex2; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastEx = ex; Logger.Debug($"CDN {server.Host}: {ex.Message}"); }
        }

        throw new Exception(
            $"Primary CDN failed for {task.Chunk.ChunkIdHex}: {lastEx?.Message}", lastEx);
    }

    public void Dispose() { }
}
