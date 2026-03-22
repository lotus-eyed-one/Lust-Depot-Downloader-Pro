using System.Collections.Concurrent;
using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Utils;
using Newtonsoft.Json;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Single persistent JSON database for everything:
///   - Downloaded game records (manifest IDs, output dirs)
///   - Per-session progress (replaces checkpoint JSON files)
///   - Download history
///   - App settings / preferences
///
/// Stored at %APPDATA%\LustsDepotDownloader\localdb.json
/// Thread-safe; progress entries are flushed at most every 30s to avoid
/// hammering the disk (same throttle the old Checkpoint used).
/// </summary>
public class LocalDatabase
{
    // ─── Singleton ────────────────────────────────────────────────────────────

    private static LocalDatabase? _instance;
    public static LocalDatabase Instance => _instance ??= new LocalDatabase();

    // ─── Storage path ─────────────────────────────────────────────────────────

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LustsDepotDownloader",
        "localdb.json");

    private readonly ReaderWriterLockSlim _lock   = new();
    private readonly object               _saveLck = new();
    private LocalDbData _data;

    // Progress save throttle — max one flush every 30 s
    private DateTime _lastProgressFlush = DateTime.MinValue;
    private readonly TimeSpan _progressFlushInterval = TimeSpan.FromSeconds(30);

    // In-memory progress sets (flushed to _data.Progress periodically)
    // Key = session key (see MakeSessionKey)
    private readonly ConcurrentDictionary<string, ConcurrentHashSet<string>> _progressCache = new();

    private LocalDatabase()
    {
        _data = Load();

        // Warm up the in-memory cache from stored progress
        foreach (var (key, entry) in _data.Progress)
        {
            var set = new ConcurrentHashSet<string>();
            foreach (var id in entry.CompletedChunks) set.Add(id);
            _progressCache[key] = set;
        }

        Logger.Debug($"DB loaded: {_data.DownloadedGames.Count} game(s), " +
                     $"{_data.Progress.Count} active session(s)");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Progress / chunk tracking  (replaces Checkpoint JSON files)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a unique key for this download session.
    /// Uses appId + a short hash of the output path so the same game
    /// downloaded to two different folders gets separate progress entries.
    /// </summary>
    public static string MakeSessionKey(uint appId, string outputDir)
    {
        // Short deterministic hash of the output path
        uint h = 2166136261u;
        foreach (char c in outputDir.ToLowerInvariant())
            h = (h ^ c) * 16777619u;
        return $"{appId}_{h:x8}";
    }

    public bool IsChunkComplete(string sessionKey, string chunkId)
    {
        if (!_progressCache.TryGetValue(sessionKey, out var set)) return false;
        return set.Contains(chunkId);
    }

    public int GetCompletedChunkCount(string sessionKey)
    {
        if (!_progressCache.TryGetValue(sessionKey, out var set)) return 0;
        return set.Count;
    }

    public void LoadProgress(string sessionKey)
    {
        // Ensure cache entry exists (may already be warm from ctor)
        _progressCache.GetOrAdd(sessionKey, _ =>
        {
            var set = new ConcurrentHashSet<string>();
            _lock.EnterReadLock();
            try
            {
                if (_data.Progress.TryGetValue(sessionKey, out var entry))
                    foreach (var id in entry.CompletedChunks) set.Add(id);
            }
            finally { _lock.ExitReadLock(); }

            if (set.Count > 0)
                Logger.Info($"Resumed: {set.Count} chunks already complete");
            return set;
        });
    }

    /// <summary>Mark a chunk complete. Flushes to DB at most every 30 seconds.</summary>
    public void MarkChunkComplete(string sessionKey, string chunkId)
    {
        var set = _progressCache.GetOrAdd(sessionKey, _ => new ConcurrentHashSet<string>());
        set.Add(chunkId);
        MaybeFlushProgress(sessionKey, set, force: false);
    }

    /// <summary>Force-flush progress to DB immediately (called on pause/cancel/complete).</summary>
    public void FlushProgress(string sessionKey)
    {
        if (!_progressCache.TryGetValue(sessionKey, out var set)) return;
        MaybeFlushProgress(sessionKey, set, force: true);
    }

    public void ClearProgress(string sessionKey)
    {
        _progressCache.TryRemove(sessionKey, out _);
        _lock.EnterWriteLock();
        try { _data.Progress.Remove(sessionKey); }
        finally { _lock.ExitWriteLock(); }
        SaveAsync();
    }

    private void MaybeFlushProgress(string sessionKey, ConcurrentHashSet<string> set, bool force)
    {
        if (!force && DateTime.UtcNow - _lastProgressFlush < _progressFlushInterval)
            return;

        lock (_saveLck)
        {
            if (!force && DateTime.UtcNow - _lastProgressFlush < _progressFlushInterval)
                return;

            _lock.EnterWriteLock();
            try
            {
                _data.Progress[sessionKey] = new ProgressEntry
                {
                    SessionKey      = sessionKey,
                    CompletedChunks = set.ToHashSet(),
                    LastSaved       = DateTime.UtcNow,
                };
            }
            finally { _lock.ExitWriteLock(); }

            _lastProgressFlush = DateTime.UtcNow;
        }

        SaveAsync();
        Logger.Debug($"Progress flushed: {set.Count} chunks ({sessionKey})");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Downloaded game records & update tracking
    // ═════════════════════════════════════════════════════════════════════════

    public void RecordDownload(uint appId, string name, string installPath,
        Dictionary<uint, ulong> depotManifestIds,
        uint buildId = 0, long totalSizeBytes = 0, int fileCount = 0,
        string branch = "public", string os = "")
    {
        _lock.EnterWriteLock();
        try
        {
            var record = new DownloadRecord
            {
                AppId            = appId,
                Name             = name,
                OutputDir        = installPath,
                Status           = DownloadStatus.Complete,
                Progress         = 100,
                CompletedAt      = DateTime.UtcNow,
                DepotManifestIds = depotManifestIds,
            };

            var idx = _data.DownloadHistory
                .FindIndex(r => r.AppId == appId && r.OutputDir == installPath);
            if (idx >= 0)
                _data.DownloadHistory[idx] = record;
            else
                _data.DownloadHistory.Add(record);

            _data.DownloadedGames[appId] = new LocalGameRecord
            {
                AppId            = appId,
                Name             = name,
                InstallPath      = installPath,
                BuildId          = buildId,
                DepotManifestIds = depotManifestIds,
                TotalSizeBytes   = totalSizeBytes,
                FileCount        = fileCount,
                Branch           = branch,
                Os               = os,
                LastDownloadedAt = DateTime.UtcNow,
            };
        }
        finally { _lock.ExitWriteLock(); }

        SaveAsync();
    }

    public LocalGameRecord? GetGameRecord(uint appId)
    {
        _lock.EnterReadLock();
        try { return _data.DownloadedGames.TryGetValue(appId, out var r) ? r : null; }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<LocalGameRecord> GetAllGameRecords()
    {
        _lock.EnterReadLock();
        try { return _data.DownloadedGames.Values.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<DownloadRecord> GetDownloadHistory()
    {
        _lock.EnterReadLock();
        try { return _data.DownloadHistory.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    public void RemoveGameRecord(uint appId)
    {
        _lock.EnterWriteLock();
        try { _data.DownloadedGames.Remove(appId); }
        finally { _lock.ExitWriteLock(); }
        SaveAsync();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Settings
    // ═════════════════════════════════════════════════════════════════════════

    public string? GetSetting(string key)
    {
        _lock.EnterReadLock();
        try { return _data.Settings.TryGetValue(key, out var v) ? v : null; }
        finally { _lock.ExitReadLock(); }
    }

    public void SetSetting(string key, string value)
    {
        _lock.EnterWriteLock();
        try { _data.Settings[key] = value; }
        finally { _lock.ExitWriteLock(); }
        SaveAsync();
    }

    public int? GetPreferredCellId()
    {
        var v = GetSetting("preferred_cell_id");
        return v != null && int.TryParse(v, out int id) ? id : null;
    }

    public void SetPreferredCellId(int cellId) =>
        SetSetting("preferred_cell_id", cellId.ToString());

    // ═════════════════════════════════════════════════════════════════════════
    // Persistence
    // ═════════════════════════════════════════════════════════════════════════

    private void SaveAsync()
    {
        _ = Task.Run(() =>
        {
            try
            {
                LocalDbData snapshot;
                _lock.EnterReadLock();
                try { snapshot = CloneData(); }
                finally { _lock.ExitReadLock(); }

                Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
                var tmp = DbPath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                File.Move(tmp, DbPath, overwrite: true);
            }
            catch (Exception ex) { Logger.Error($"DB save failed: {ex.Message}"); }
        });
    }

    private LocalDbData CloneData()
    {
        // Shallow-safe: called with read lock held
        return new LocalDbData
        {
            DownloadedGames = new Dictionary<uint, LocalGameRecord>(_data.DownloadedGames),
            DownloadHistory = new List<DownloadRecord>(_data.DownloadHistory),
            Settings        = new Dictionary<string, string>(_data.Settings),
            Progress        = new Dictionary<string, ProgressEntry>(_data.Progress),
        };
    }

    private static LocalDbData Load()
    {
        if (!File.Exists(DbPath)) return new LocalDbData();
        try
        {
            var json = File.ReadAllText(DbPath);
            return JsonConvert.DeserializeObject<LocalDbData>(json) ?? new LocalDbData();
        }
        catch (Exception ex)
        {
            Logger.Warn($"DB load failed ({ex.Message}) — starting fresh");
            return new LocalDbData();
        }
    }
}

// ─── Stored models ────────────────────────────────────────────────────────────

public class LocalDbData
{
    [JsonProperty("downloaded_games")]
    public Dictionary<uint, LocalGameRecord> DownloadedGames { get; set; } = new();

    [JsonProperty("download_history")]
    public List<DownloadRecord> DownloadHistory { get; set; } = new();

    [JsonProperty("settings")]
    public Dictionary<string, string> Settings { get; set; } = new();

    [JsonProperty("progress")]
    public Dictionary<string, ProgressEntry> Progress { get; set; } = new();
}

public class ProgressEntry
{
    [JsonProperty("session_key")]
    public string SessionKey { get; set; } = "";

    [JsonProperty("completed_chunks")]
    public HashSet<string> CompletedChunks { get; set; } = new();

    [JsonProperty("last_saved")]
    public DateTime LastSaved { get; set; }
}

public class LocalGameRecord
{
    [JsonProperty("app_id")]
    public uint AppId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("install_path")]
    public string InstallPath { get; set; } = "";

    // Backward-compat alias
    [JsonIgnore]
    public string OutputDir { get => InstallPath; set => InstallPath = value; }

    [JsonProperty("build_id")]
    public uint BuildId { get; set; }

    [JsonProperty("depot_manifest_ids")]
    public Dictionary<uint, ulong> DepotManifestIds { get; set; } = new();

    [JsonProperty("total_size_bytes")]
    public long TotalSizeBytes { get; set; }

    [JsonProperty("file_count")]
    public int FileCount { get; set; }

    [JsonProperty("branch")]
    public string Branch { get; set; } = "public";

    [JsonProperty("os")]
    public string Os { get; set; } = "";

    [JsonProperty("last_downloaded_at")]
    public DateTime LastDownloadedAt { get; set; }

    [JsonProperty("last_verified_at")]
    public DateTime? LastVerifiedAt { get; set; }

    [JsonProperty("verify_status")]
    public string VerifyStatus { get; set; } = "";
}
