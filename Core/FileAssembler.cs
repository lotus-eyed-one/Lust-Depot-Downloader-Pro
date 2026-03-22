using System.Collections.Concurrent;
using LustsDepotDownloaderPro.Models;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Thread-safe file assembler that keeps file handles open and cached,
/// eliminating the open/close overhead on every chunk write.
/// Each file gets its own SemaphoreSlim so workers writing to different
/// files never block each other — only concurrent writes to the SAME file
/// are serialized (necessary for correct offset-based writing).
/// </summary>
public class FileAssembler : IDisposable
{
    private readonly string _baseDir;

    // Cache open FileStream handles — avoids open/close per chunk
    private readonly ConcurrentDictionary<string, CachedFileHandle> _handles = new();

    // Per-file locks — workers on DIFFERENT files never block each other
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private bool _disposed;

    public FileAssembler(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    public async Task WriteChunkAsync(ManifestFile file, ManifestChunk chunk, byte[] data)
    {
        string filePath = Path.Combine(_baseDir, file.FileName.Replace('/', Path.DirectorySeparatorChar));

        // Ensure directory exists (only costs anything the first time)
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Per-file lock — only serializes concurrent writes to the same file
        var fileLock = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync();
        try
        {
            // Get or open a cached handle for this file
            var handle = _handles.GetOrAdd(filePath, path =>
            {
                // Pre-allocate the full file size so the OS doesn't fragment it
                // and random-offset writes don't extend the file on every chunk
                var fs = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1 << 20,  // 1 MB buffer — was 80 KB
                    useAsync: true);

                if (file.Size > 0 && fs.Length < (long)file.Size)
                    fs.SetLength((long)file.Size);

                return new CachedFileHandle(fs);
            });

            handle.Stream.Seek((long)chunk.Offset, SeekOrigin.Begin);
            await handle.Stream.WriteAsync(data, 0, data.Length);
            // No FlushAsync per chunk — OS page cache batches writes automatically.
            // The handle is flushed and closed in Dispose/FinalizeFileAsync.
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task FinalizeFileAsync(ManifestFile file)
    {
        string filePath = Path.Combine(_baseDir, file.FileName.Replace('/', Path.DirectorySeparatorChar));

        // Flush and close the cached handle so the file is readable
        if (_handles.TryRemove(filePath, out var handle))
        {
            await handle.Stream.FlushAsync();
            handle.Stream.Dispose();
        }

        if (!File.Exists(filePath))
        {
            Utils.Logger.Warn($"File not found for finalization: {filePath}");
            return;
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length != (long)file.Size)
            Utils.Logger.Warn($"Size mismatch: {file.FileName} (expected {file.Size}, actual {fileInfo.Length})");

        // Set executable permission on Unix
        if ((file.Flags & 0x40) != 0 && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            try
            {
                await Utils.ProcessRunner.RunAsync("chmod", $"+x \"{filePath}\"");
            }
            catch (Exception ex)
            {
                Utils.Logger.Debug($"chmod failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Flush and close all open handles
        foreach (var kv in _handles)
        {
            try
            {
                kv.Value.Stream.Flush();
                kv.Value.Stream.Dispose();
            }
            catch { /* best-effort */ }
        }
        _handles.Clear();

        foreach (var kv in _fileLocks)
            kv.Value.Dispose();
        _fileLocks.Clear();
    }

    private sealed class CachedFileHandle(FileStream stream)
    {
        public FileStream Stream { get; } = stream;
    }
}