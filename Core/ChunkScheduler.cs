using System.Collections.Concurrent;
using LustsDepotDownloaderPro.Models;

namespace LustsDepotDownloaderPro.Core;

public class ChunkScheduler
{
    private readonly ConcurrentQueue<ChunkTask> _queue   = new();
    private readonly ConcurrentDictionary<string, ChunkTask> _retry = new();
    private int _totalScheduled;
    private bool _schedulingComplete;

    public int  PendingCount     => _queue.Count + _retry.Count;
    public int  TotalScheduled   => _totalScheduled;
    public bool IsComplete       => _schedulingComplete && _queue.IsEmpty && _retry.IsEmpty;

    public void Enqueue(ChunkTask task)
    {
        _queue.Enqueue(task);
        Interlocked.Increment(ref _totalScheduled);
    }

    public bool TryDequeue(out ChunkTask? task)
    {
        // Drain retries first (they have back-off already applied)
        foreach (var kvp in _retry.ToArray())
        {
            if (_retry.TryRemove(kvp.Key, out task))
                return true;
        }
        return _queue.TryDequeue(out task);
    }

    public async Task MarkFailedAsync(ChunkTask task)
    {
        task.RetryCount++;
        if (task.RetryCount > 10)
        {
            Utils.Logger.Error(
                $"Chunk {task.Chunk.ChunkIdHex} permanently failed after {task.RetryCount} attempts");
            return;
        }
        // Exponential back-off: 2s, 4s, 8s … capped at 30s
        int delayMs = Math.Min(2000 * (int)Math.Pow(2, task.RetryCount - 1), 30_000);
        await Task.Delay(delayMs);
        _retry[task.Chunk.ChunkIdHex] = task;
    }

    // Sync overload for callers that can't await (kept for compatibility)
    public void MarkFailed(ChunkTask task)
    {
        task.RetryCount++;
        if (task.RetryCount > 10)
        {
            Utils.Logger.Error(
                $"Chunk {task.Chunk.ChunkIdHex} permanently failed after {task.RetryCount} attempts");
            return;
        }
        int delayMs = Math.Min(2000 * (int)Math.Pow(2, task.RetryCount - 1), 30_000);
        // Use fire-and-forget async re-queue instead of blocking Wait()
        _ = Task.Delay(delayMs).ContinueWith(_ => _retry[task.Chunk.ChunkIdHex] = task);
    }

    public void MarkSchedulingComplete() => _schedulingComplete = true;
}
