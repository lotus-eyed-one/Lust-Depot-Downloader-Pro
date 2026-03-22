using System.Collections.Concurrent;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Progress tracker backed by LocalDatabase instead of a separate JSON file.
/// All chunk progress is stored in the single localdb.json — no checkpoint files
/// scattered next to game folders.
///
/// Each download session gets a key derived from (appId, outputDir) so the same
/// game downloaded to two different folders has independent progress.
/// </summary>
public class Checkpoint
{
    private readonly string _sessionKey;
    private readonly LocalDatabase _db;

    // Mirror of the DB set in memory for fast O(1) lookups during download
    private readonly ConcurrentHashSet<string> _completed = new();

    public string SessionKey => _sessionKey;
    public int    Count      => _completed.Count;

    private Checkpoint(string sessionKey, LocalDatabase db)
    {
        _sessionKey = sessionKey;
        _db         = db;
    }

    /// <summary>
    /// Load (or create) a progress entry from the DB for this session.
    /// </summary>
    public static Checkpoint Load(uint appId, string outputDir)
    {
        var db  = LocalDatabase.Instance;
        var key = LocalDatabase.MakeSessionKey(appId, outputDir);
        db.LoadProgress(key);

        var cp = new Checkpoint(key, db);

        // Warm the in-memory set from the DB
        // (DB already loaded chunks into its own ConcurrentHashSet internally,
        //  but we need our own copy for the hot-path IsChunkComplete check)
        // Progress is loaded into the DB's in-memory cache by LoadProgress().
        // IsChunkComplete() routes through that cache — no separate copy needed.
        return cp;
    }

    public void MarkChunkComplete(string chunkId)
    {
        _completed.Add(chunkId);
        _db.MarkChunkComplete(_sessionKey, chunkId);
    }

    /// <summary>
    /// Hot path — checks the in-memory set first, falls back to DB.
    /// O(1) for both.
    /// </summary>
    public bool IsChunkComplete(string chunkId) =>
        _completed.Contains(chunkId) || _db.IsChunkComplete(_sessionKey, chunkId);

    /// <summary>Force a DB flush (called on pause / Ctrl+C).</summary>
    public void Save() => _db.FlushProgress(_sessionKey);

    /// <summary>Remove progress from DB once download finishes successfully.</summary>
    public void Clear() => _db.ClearProgress(_sessionKey);
}

// ─── Shared ConcurrentHashSet ─────────────────────────────────────────────────

public class ConcurrentHashSet<T> where T : notnull
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<T, byte> _d = new();
    public void Add(T i)      => _d.TryAdd(i, 0);
    public bool Contains(T i) => _d.ContainsKey(i);
    public HashSet<T> ToHashSet() => _d.Keys.ToHashSet();
    public int Count          => _d.Count;
}
