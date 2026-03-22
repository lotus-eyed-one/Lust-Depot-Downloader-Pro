using System.IO.Compression;
using System.Security.Cryptography;
using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Direct-HTTP fallback download engine.
/// Fetches chunks from Steam CDN raw: GET https://{host}/depot/{depotId}/chunk/{chunkHex}
/// Then performs the AES decrypt + decompress pipeline locally.
///
/// Decrypt: AES-ECB(IV) → AES-CBC(body) — same as SteamKit2's internal pipeline.
/// Decompress:
///   VZip (0x56 0x5A magic) → extract inner payload, decompress as deflate
///   Plain zlib/deflate     → strip 2-byte header if present, DeflateStream
///   LZMA                   → SteamKit2 handles LZMA internally when going via
///                            DownloadDepotChunkAsync; for the raw fallback path
///                            LZMA games should route through primary workers.
///                            If we encounter LZMA here, we let SteamKit2 retry.
/// </summary>
public class FallbackDownloader
{
    private static readonly string[] PublicCdnHosts =
    {
        "cs.steamcontent.com",
        "cdn.akamai.steamstatic.com",
        "steampipe.akamaized.net",
        "cdn.steamcontent.com",
        "cdn1.steamcontent.com",
        "cdn2.steamcontent.com",
        "cdn3.steamcontent.com",
    };

    // Pool of HTTP clients — rotated per-request for higher throughput
    private static readonly HttpClient[] _httpPool = Enumerable.Range(0, 4).Select(_ =>
        new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect  = true,
            MaxConnectionsPerServer = 32,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestHeaders = { { "User-Agent", "Valve/Steam HTTP Client 1.0" } }
        }).ToArray();
    private static int _poolIdx;
    private static HttpClient Http =>
        _httpPool[(uint)System.Threading.Interlocked.Increment(ref _poolIdx) % _httpPool.Length];

    private readonly IReadOnlyList<string> _cdnHosts;

    public FallbackDownloader(IEnumerable<string>? authenticatedHosts = null)
    {
        var hosts = authenticatedHosts?.ToList() ?? new List<string>();
        foreach (var h in PublicCdnHosts)
            if (!hosts.Contains(h, StringComparer.OrdinalIgnoreCase))
                hosts.Add(h);
        _cdnHosts = hosts;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<byte[]> DownloadChunkAsync(
        uint depotId, ManifestChunk chunk, byte[]? depotKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chunk.ChunkIdHex))
            throw new ArgumentException("Chunk has no ID");

        Exception? lastEx = null;
        foreach (var host in _cdnHosts.Take(6))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await FetchRawAsync(host, depotId, chunk.ChunkIdHex, ct);
                return DecryptAndDecompress(raw, depotKey);
            }
            catch (OperationCanceledException) { throw; }
            catch (NotSupportedException ex)
            {
                // LZMA — can't handle in fallback path; bubble up so primary handles it
                throw new Exception(
                    $"Chunk {chunk.ChunkIdHex} uses LZMA compression — " +
                    "route through primary (SteamKit2) workers", ex);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Logger.Debug($"[fallback] {host}: {ex.Message}");
            }
        }
        throw new Exception(
            $"[fallback] all hosts failed for {chunk.ChunkIdHex}: {lastEx?.Message}", lastEx);
    }

    // ─── Fetch ────────────────────────────────────────────────────────────────

    private static async Task<byte[]> FetchRawAsync(
        string host, uint depotId, string chunkIdHex, CancellationToken ct)
    {
        using var cts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var url  = $"https://{host}/depot/{depotId}/chunk/{chunkIdHex}";
        var resp = await Http.GetAsync(url, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cts.Token);
    }

    // ─── Decrypt + Decompress ─────────────────────────────────────────────────

    private static byte[] DecryptAndDecompress(byte[] raw, byte[]? depotKey)
    {
        byte[] payload = depotKey?.Length == 32 ? AesDecrypt(raw, depotKey) : raw;
        return Decompress(payload);
    }

    private static byte[] AesDecrypt(byte[] cipher, byte[] key)
    {
        if (cipher.Length < 32)
            throw new InvalidDataException("Chunk data too short for AES header");

        // Recover IV: decrypt first 16 bytes with AES-ECB
        using var ecb = Aes.Create();
        ecb.Key = key; ecb.Mode = CipherMode.ECB; ecb.Padding = PaddingMode.None;
        byte[] iv = ecb.CreateDecryptor().TransformFinalBlock(cipher, 0, 16);

        // Decrypt body with AES-CBC + recovered IV
        using var cbc = Aes.Create();
        cbc.Key = key; cbc.IV = iv; cbc.Mode = CipherMode.CBC; cbc.Padding = PaddingMode.PKCS7;
        return cbc.CreateDecryptor().TransformFinalBlock(cipher, 16, cipher.Length - 16);
    }

    private static byte[] Decompress(byte[] data)
    {
        if (data.Length < 2) return data;

        // VZip: magic 0x56 0x5A
        if (data[0] == 0x56 && data[1] == 0x5A)
            return VZipDecompress(data);

        // LZMA: first byte 0x5D — can't decompress without SevenZipSharp/SDK
        if (data[0] == 0x5D && data.Length > 13)
            throw new NotSupportedException("LZMA compression");

        // zlib / raw deflate
        return ZlibDecompress(data);
    }

    // VZip layout: [0x56][0x5A][4-byte version][1-byte compression-type][payload][CRC32 4][uncompSize 4]
    private static byte[] VZipDecompress(byte[] data)
    {
        if (data.Length < 15) throw new InvalidDataException("VZip too short");
        // byte 6 = compression type: 0 = deflate, 1 = LZMA
        if (data[6] == 1) throw new NotSupportedException("VZip/LZMA compression");
        byte[] payload = data[7..(data.Length - 8)];
        return ZlibDecompress(payload);
    }

    private static byte[] ZlibDecompress(byte[] data)
    {
        // Skip 2-byte zlib header (0x78 xx) if present
        int offset = (data.Length >= 2 && data[0] == 0x78) ? 2 : 0;
        using var ms   = new MemoryStream(data, offset, data.Length - offset);
        using var def  = new DeflateStream(ms, CompressionMode.Decompress);
        using var out_ = new MemoryStream();
        def.CopyTo(out_);
        return out_.ToArray();
    }
}
