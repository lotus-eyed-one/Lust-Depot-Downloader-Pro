namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Thread-safe progress tracker.
///
/// Resume fix: on construction pass already-completed bytes so the percentage
/// accounts for previously downloaded chunks, not just the remaining ones.
/// The progress bar will start at the correct % instead of resetting to 0.
///
/// Speed uses a 5-second sliding window so it reflects current throughput.
/// </summary>
public class GlobalProgress
{
    private long _downloaded;
    private long _total;

    // Pre-seeded for resume: bytes already complete BEFORE this session started
    private long _alreadyBytes;
    private long _alreadyBytes2; // set via SeedCompleted

    private readonly DateTime _startTime = DateTime.UtcNow;

    private readonly Queue<(DateTime t, long bytes)> _window = new();
    private readonly object _windowLock = new();

    /// <summary>
    /// Create a progress tracker.
    /// </summary>
    /// <param name="alreadyCompletedBytes">
    /// Bytes already downloaded in a previous session (for resume).
    /// These count toward the percentage but not toward the current session speed.
    /// </param>
    public GlobalProgress(long alreadyCompletedBytes = 0)
    {
        _alreadyBytes = alreadyCompletedBytes;
        _downloaded   = 0; // bytes downloaded THIS session
    }

    /// <summary>Add bytes to the total (includes both done + pending).</summary>
    public void AddTotal(long bytes) => Interlocked.Add(ref _total, bytes);

    /// <summary>
    /// Pre-seed already-completed bytes (for resume).
    /// Call ONCE before workers start, with the total bytes of completed chunks.
    /// </summary>
    public void SeedCompleted(long alreadyCompletedBytes)
    {
        // Store as the "already done" offset — counts toward % but not current speed
        Interlocked.Add(ref _alreadyBytes2, alreadyCompletedBytes);
    }

    /// <summary>Report that bytes were just downloaded in this session.</summary>
    public void ReportProgress(long bytes)
    {
        var now   = DateTime.UtcNow;
        var cumul = Interlocked.Add(ref _downloaded, bytes);
        lock (_windowLock)
        {
            _window.Enqueue((now, cumul));
            while (_window.Count > 1 && (now - _window.Peek().t).TotalSeconds > 5)
                _window.Dequeue();
        }
    }

    public ProgressSnapshot GetSnapshot()
    {
        var now          = DateTime.UtcNow;
        var thisSession  = Interlocked.Read(ref _downloaded);
        var total        = Interlocked.Read(ref _total);
        var totalDone    = _alreadyBytes + _alreadyBytes2 + thisSession;
        var elapsed      = (now - _startTime).TotalSeconds;

        // Speed from rolling window (only counts this session's bytes — accurate)
        double speedMBps = 0;
        lock (_windowLock)
        {
            if (_window.Count >= 2)
            {
                var oldest     = _window.Peek();
                double winSec  = (now - oldest.t).TotalSeconds;
                long   winBytes = thisSession - oldest.bytes;
                if (winSec > 0) speedMBps = (winBytes / 1_048_576.0) / winSec;
            }
            else if (elapsed > 0 && thisSession > 0)
            {
                speedMBps = (thisSession / 1_048_576.0) / elapsed;
            }
        }

        // ETA based on remaining bytes vs current speed
        double etaSeconds = 0;
        if (speedMBps > 0 && total > totalDone)
        {
            double remainingMB = (total - totalDone) / 1_048_576.0;
            etaSeconds = remainingMB / speedMBps;
        }

        return new ProgressSnapshot
        {
            DownloadedMB   = totalDone / 1_048_576.0,
            TotalMB        = total     / 1_048_576.0,
            Percent        = total == 0 ? 0 : Math.Min(100.0, totalDone * 100.0 / total),
            SpeedMBps      = speedMBps,
            EtaSeconds     = etaSeconds,
            ElapsedSeconds = elapsed
        };
    }
}

public class ProgressSnapshot
{
    public double DownloadedMB   { get; set; }
    public double TotalMB        { get; set; }
    public double Percent        { get; set; }
    public double SpeedMBps      { get; set; }
    public double EtaSeconds     { get; set; }
    public double ElapsedSeconds { get; set; }
}
