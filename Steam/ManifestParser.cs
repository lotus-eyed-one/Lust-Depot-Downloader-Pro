using SteamKit2;
using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// FIX: The original implementation tried to hand-roll protobuf parsing with wrong field numbers,
/// a wrong Crc type (byte[] instead of uint32), and skipped the Steam binary header.
/// The correct approach is to let SteamKit2's DepotManifest handle all of that, then
/// convert its output to our internal model.
/// </summary>
public static class ManifestParser
{
    /// <summary>
    /// Convert a fully-parsed SteamKit2 <see cref="DepotManifest"/> into our internal file list.
    /// SteamKit2 has already handled: binary header, protobuf deserialisation, AES decryption
    /// of file names, and decompression. Nothing left to do except shape-shift the data.
    /// </summary>
    public static List<ManifestFile> FromDepotManifest(DepotManifest manifest)
    {
        var files = new List<ManifestFile>();

        if (manifest.Files == null)
        {
            Logger.Warn("Manifest has no file list — was it downloaded correctly?");
            return files;
        }

        foreach (var entry in manifest.Files)
        {
            // Skip directory entries — they carry no chunk data
            if ((entry.Flags & EDepotFileFlag.Directory) != 0) continue;

            var file = new ManifestFile
            {
                FileName = entry.FileName,
                Size     = entry.TotalSize,
                Flags    = (int)entry.Flags,
                FileHash = entry.FileHash,
                Chunks   = new List<ManifestChunk>()
            };

            foreach (var chunk in entry.Chunks)
            {
                file.Chunks.Add(new ManifestChunk
                {
                    ChunkId            = chunk.ChunkID,
                    ChunkIdHex         = HexEncode(chunk.ChunkID),
                    Offset             = chunk.Offset,
                    CompressedLength   = chunk.CompressedLength,
                    UncompressedLength = chunk.UncompressedLength,
                    // In SteamKit2 3.x, ChunkData.Checksum is uint (was byte[] in 2.x)
                    Checksum           = chunk.Checksum
                });
            }

            files.Add(file);
        }

        Logger.Info($"Parsed {files.Count} files, " +
                    $"{files.Sum(f => f.Chunks.Count)} total chunks");
        return files;
    }

    /// <summary>
    /// Load a raw .manifest binary saved to disk and parse it.
    /// </summary>
    public static List<ManifestFile>? LoadFromFile(string path, byte[]? depotKey = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                Logger.Error($"Manifest file not found: {path}");
                return null;
            }

            var data     = File.ReadAllBytes(path);
            var manifest = DepotManifest.Deserialize(data);

            if (depotKey != null)
                manifest.DecryptFilenames(depotKey);

            return FromDepotManifest(manifest);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load manifest from file: {ex.Message}");
            return null;
        }
    }

    private static string HexEncode(byte[]? bytes)
        => bytes == null ? "" :
           BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
