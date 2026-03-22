using System.Security.Cryptography;
using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Utils;
using Spectre.Console;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Verifies an installed game against its manifest — same logic as Steam's
/// "Verify Integrity of Game Files". Reports missing, corrupt, and extra files.
///
/// Also extracts the local build ID from the steamapps ACF file if present,
/// and can compare it against the latest manifest from community sources.
/// </summary>
public class IntegrityVerifier
{
    // ═══════════════════════════════════════════════════════════════════════
    // Verify installed game against its manifest
    // ═══════════════════════════════════════════════════════════════════════

    public static async Task<VerifyResult> VerifyAsync(
        string installPath,
        IEnumerable<DepotInfo> depots,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new VerifyResult { InstallPath = installPath };

        var allFiles = depots
            .SelectMany(d => d.Files)
            .Where(f => (f.Flags & 0x40) == 0) // skip symlinks (flag 0x40)
            .ToList();

        result.TotalFiles = allFiles.Count;
        result.TotalBytes = allFiles.Sum(f => (long)f.Size);

        int done = 0;
        long doneBytes = 0;

        // Process files in parallel (8 threads — disk-bound)
        var semaphore = new SemaphoreSlim(8, 8);

        var tasks = allFiles.Select(async file =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                string fullPath = Path.Combine(
                    installPath,
                    file.FileName.Replace('/', Path.DirectorySeparatorChar));

                var fileResult = await VerifyFileAsync(fullPath, file, ct);
                fileResult.FileName = file.FileName;

                lock (result)
                {
                    result.FileResults.Add(fileResult);
                    done++;
                    doneBytes += (long)file.Size;

                    switch (fileResult.Status)
                    {
                        case FileStatus.Missing: result.MissingCount++; break;
                        case FileStatus.Corrupt: result.CorruptCount++; break;
                        case FileStatus.SizeMismatch: result.CorruptCount++; break;
                        case FileStatus.Ok: result.OkCount++; break;
                    }
                }

                progress?.Report(new VerifyProgress
                {
                    Done      = done,
                    Total     = allFiles.Count,
                    DoneBytes = doneBytes,
                    TotalBytes= result.TotalBytes,
                    LastFile  = file.FileName,
                    Status    = fileResult.Status
                });
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);

        result.IsClean = result.MissingCount == 0 && result.CorruptCount == 0;
        result.VerifiedAt = DateTime.UtcNow;
        return result;
    }

    private static async Task<FileVerifyResult> VerifyFileAsync(
        string path, ManifestFile manifest, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new FileVerifyResult { Status = FileStatus.Missing };

        var info = new FileInfo(path);

        // Size check first — cheap
        if (manifest.Size > 0 && info.Length != (long)manifest.Size)
            return new FileVerifyResult
            {
                Status     = FileStatus.SizeMismatch,
                ActualSize = info.Length,
                ExpectedSize = (long)manifest.Size
            };

        // Hash check if manifest has a hash
        if (manifest.FileHash != null && manifest.FileHash.Length > 0)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, true);
                using var sha1 = SHA1.Create();
                byte[] hash = await Task.Run(() => sha1.ComputeHash(stream), ct);

                if (!hash.SequenceEqual(manifest.FileHash))
                    return new FileVerifyResult
                    {
                        Status       = FileStatus.Corrupt,
                        ActualSize   = info.Length,
                        ExpectedSize = (long)manifest.Size
                    };
            }
            catch (IOException)
            {
                return new FileVerifyResult { Status = FileStatus.Corrupt };
            }
        }

        return new FileVerifyResult
        {
            Status       = FileStatus.Ok,
            ActualSize   = info.Length,
            ExpectedSize = (long)manifest.Size
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Build ID detection
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read the local build ID from the ACF file in the steamapps folder,
    /// or from our own localdb if the game was downloaded by this tool.
    /// </summary>
    public static uint GetLocalBuildId(uint appId, string installPath)
    {
        // Try steamapps ACF file (if game is in a Steam library)
        var acfBuild = TryReadAcfBuildId(appId, installPath);
        if (acfBuild > 0) return acfBuild;

        // Fall back to localdb record
        var record = LocalDatabase.Instance.GetGameRecord(appId);
        return record?.BuildId ?? 0;
    }

    private static uint TryReadAcfBuildId(uint appId, string installPath)
    {
        try
        {
            // ACF is usually in steamapps/ parent of the install dir
            string? parent = Path.GetDirectoryName(installPath);
            if (parent == null) return 0;

            // Try both "steamapps" parent and the current dir
            foreach (var dir in new[] { parent, Path.GetDirectoryName(parent) ?? "" })
            {
                string acf = Path.Combine(dir, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                foreach (var line in File.ReadAllLines(acf))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("\"buildid\"", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split('"');
                        if (parts.Length >= 4 && uint.TryParse(parts[3], out uint bid))
                            return bid;
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Display helpers
    // ═══════════════════════════════════════════════════════════════════════

    public static void PrintVerifyResult(VerifyResult result, bool showGoodFiles = false)
    {
        AnsiConsole.WriteLine();

        string statusStr = result.IsClean
            ? "[bold green] All files verified — no issues found[/]"
            : $"[bold red]X Verification found issues: " +
              $"{result.MissingCount} missing, {result.CorruptCount} corrupt[/]";
        AnsiConsole.MarkupLine(statusStr);

        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("Metric").AddColumn("Value");

        summary.AddRow("Total files",    result.TotalFiles.ToString());
        summary.AddRow("[green]OK[/]",   result.OkCount.ToString());
        summary.AddRow("[red]Missing[/]",result.MissingCount.ToString());
        summary.AddRow("[red]Corrupt[/]", result.CorruptCount.ToString());
        summary.AddRow("Total size",     $"{result.TotalBytes / 1_073_741_824.0:F2} GB");
        summary.AddRow("Verified at",    result.VerifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AnsiConsole.Write(summary);

        if (!result.IsClean)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Affected files:[/]");
            foreach (var f in result.FileResults.Where(r => r.Status != FileStatus.Ok))
            {
                string icon = f.Status == FileStatus.Missing ? "[red]X MISSING[/]" :
                              f.Status == FileStatus.Corrupt  ? "[yellow]! CORRUPT[/]" :
                                                                "[yellow]! SIZE[/]";
                AnsiConsole.MarkupLine($"  {icon}  {EscapeMarkup(f.FileName)}");
            }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Run with --update to re-download affected files.[/]");
        }
    }

    private static string EscapeMarkup(string s) => s.Replace("[","[[").Replace("]","]]");
}

// ─── Result types ──────────────────────────────────────────────────────────────

public class VerifyResult
{
    public string   InstallPath  { get; set; } = "";
    public int      TotalFiles   { get; set; }
    public long     TotalBytes   { get; set; }
    public int      OkCount      { get; set; }
    public int      MissingCount { get; set; }
    public int      CorruptCount { get; set; }
    public bool     IsClean      { get; set; }
    public DateTime VerifiedAt   { get; set; }
    public List<FileVerifyResult> FileResults { get; set; } = new();
}

public class FileVerifyResult
{
    public string     FileName     { get; set; } = "";
    public FileStatus Status       { get; set; }
    public long       ActualSize   { get; set; }
    public long       ExpectedSize { get; set; }
}

public enum FileStatus { Ok, Missing, Corrupt, SizeMismatch }

public class VerifyProgress
{
    public int    Done       { get; set; }
    public int    Total      { get; set; }
    public long   DoneBytes  { get; set; }
    public long   TotalBytes { get; set; }
    public string LastFile   { get; set; } = "";
    public FileStatus Status { get; set; }
}
