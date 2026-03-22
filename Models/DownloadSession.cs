namespace LustsDepotDownloaderPro.Models;

public class DownloadSession
{
    public uint   AppId   { get; set; }
    public string AppName { get; set; } = "";

    /// <summary>Steam build ID for this app — from PICS or ACF.</summary>
    public uint BuildId { get; set; }
    public string OutputDir { get; set; } = "";

    /// <summary>
    /// Unique key used to look up progress in LocalDatabase.
    /// Derived from (AppId, OutputDir) — set by DownloadEngine on construction.
    /// </summary>
    public string SessionKey { get; set; } = "";

    public int  MaxDownloads      { get; set; } = 8;
    public int  FallbackWorkers   { get; set; } = -1;  // -1 = auto (25%)
    public bool ValidateChecksums { get; set; }
    public bool WasCancelled      { get; set; }

    public List<DepotInfo>    Depots      { get; set; } = new();
    public HashSet<string>    FileFilters { get; set; } = new();
    public DateTime           StartTime   { get; set; } = DateTime.UtcNow;
}

public class DepotInfo
{
    public uint   DepotId    { get; set; }
    public string DepotName  { get; set; } = "";
    public byte[]? DepotKey  { get; set; }
    public ulong  ManifestId { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public class ManifestFile
{
    public string FileName  { get; set; } = "";
    public ulong  Size      { get; set; }
    public byte[]? FileHash { get; set; }
    public int    Flags     { get; set; }
    public List<ManifestChunk> Chunks { get; set; } = new();
}

public class ManifestChunk
{
    public string  ChunkIdHex         { get; set; } = "";
    public byte[]? ChunkId            { get; set; }
    public ulong   Offset             { get; set; }
    public uint    CompressedLength   { get; set; }
    public uint    UncompressedLength { get; set; }
    public uint    Checksum           { get; set; }
}

public class ChunkTask
{
    public uint          DepotId    { get; set; }
    public ManifestChunk Chunk      { get; set; } = null!;
    public ManifestFile  File       { get; set; } = null!;
    public uint          Size       { get; set; }
    public int           RetryCount { get; set; }
}

public class DownloadStatistics
{
    public double DownloadedMB    { get; set; }
    public double TotalMB         { get; set; }
    public double Percent         { get; set; }
    public double SpeedMBps       { get; set; }
    public int    CompletedChunks { get; set; }
    public int    TotalChunks     { get; set; }
    public bool   IsCompleted     { get; set; }
    public bool   IsPaused        { get; set; }
}
