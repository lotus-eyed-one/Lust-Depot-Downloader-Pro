using System.Collections.Concurrent;

namespace LustsDepotDownloaderPro.Utils;

public static class Logger
{
    private static bool _debugEnabled = false;
    private static readonly object _lock = new();
    private static StreamWriter? _logWriter;
    private static readonly ConcurrentQueue<string> _logQueue = new();

    /// <summary>
    /// When true, Info() goes to file only — not console.
    /// Set automatically when --debug is not passed. Warnings + errors always show.
    /// </summary>
    public static bool QuietMode { get; set; } = false;

    /// <summary>
    /// When true, Info() AND Warn() skip the console (during live TUI progress).
    /// Errors still always print. File logging is unaffected.
    /// </summary>
    public static bool SilentMode { get; set; } = false;

    public static void Initialize(bool debug = false)
    {
        _debugEnabled = debug;

        try
        {
            string logDir  = Path.Combine(Environment.CurrentDirectory, "logs");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, $"download_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            _logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };

            // Background file writer — disk I/O never blocks main thread
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    if (_logQueue.TryDequeue(out var msg))
                        await _logWriter.WriteLineAsync(msg);
                    else
                        await Task.Delay(50);
                }
            });
        }
        catch { /* non-fatal */ }

        Info("Logger initialized");
    }

    // ─── Public API ───────────────────────────────────────────────────────

    public static void Info(string message)
    {
        QueueForFile("INFO", message);
        if (SilentMode || QuietMode) return;
        WriteConsole("INFO", message, ConsoleColor.White);
    }

    public static void Warn(string message)
    {
        QueueForFile("WARN", message);
        if (SilentMode) return;
        WriteConsole("WARN", message, ConsoleColor.Yellow);
    }

    public static void Error(string message)
    {
        QueueForFile("ERROR", message);
        // Errors ALWAYS reach console regardless of mode
        WriteConsole("ERROR", message, ConsoleColor.Red);
    }

    public static void Debug(string message)
    {
        if (!_debugEnabled) return;
        QueueForFile("DEBUG", message);
        WriteConsole("DEBUG", message, ConsoleColor.Gray);
    }

    public static void Success(string message)
    {
        QueueForFile("SUCCESS", message);
        if (SilentMode) return;
        WriteConsole("SUCCESS", message, ConsoleColor.Green);
    }

    // ─── Internals ────────────────────────────────────────────────────────

    private static void QueueForFile(string level, string message)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        _logQueue.Enqueue($"[{ts}] [{level}] {message}");
    }

    private static void WriteConsole(string level, string message, ConsoleColor color)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (_lock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{ts}] [{level}] {message}");
            Console.ResetColor();
        }
    }

    public static void Dispose() => _logWriter?.Dispose();
}
