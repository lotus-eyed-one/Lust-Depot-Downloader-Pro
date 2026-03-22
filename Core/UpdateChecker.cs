using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Steam;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Core;

/// <summary>
/// Checks whether a previously downloaded game has updates available.
///
/// Equivalent to Cirno's check_for_update_steam_game_info / start_update flow:
///
///   1. Load local manifest IDs from LocalDatabase (stored at last download time)
///   2. Fetch the latest manifest IDs from all community sources (ManifestSourceFetcher)
///   3. Compare per depot — if any manifest ID changed, update is available
///   4. Optionally start a download of only the changed depots
/// </summary>
public class UpdateChecker
{
    private readonly ManifestSourceFetcher _fetcher;
    private readonly LocalDatabase        _db;

    public UpdateChecker(string? githubApiKey = null)
    {
        // Use all available tokens (CLI + both baked-in)
        var tokens = new[] { githubApiKey }
            .Concat(Utils.EmbeddedConfig.GitHubTokens)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct()
            .ToArray();
        _fetcher = new ManifestSourceFetcher(tokens);
        _db      = LocalDatabase.Instance;
    }

    // ─── Check a single app ───────────────────────────────────────────────────

    /// <summary>
    /// Check if a downloaded game has an update available.
    /// Returns an UpdateCheckResult describing which depots changed.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(uint appId)
    {
        var record = _db.GetGameRecord(appId);
        if (record == null)
        {
            return new UpdateCheckResult
            {
                AppId   = appId,
                Message = $"App {appId} not in local database. Download it first.",
            };
        }

        Logger.Info($"Checking for updates: {record.Name} (AppID {appId})...");

        // Fetch latest manifest IDs from all community sources
        var latest = await _fetcher.FetchAsync(appId);
        if (latest == null || latest.Manifests.Count == 0)
        {
            return new UpdateCheckResult
            {
                AppId   = appId,
                Name    = record.Name,
                Message = "No community manifest data found — cannot check for updates.",
            };
        }

        var result = new UpdateCheckResult
        {
            AppId = appId,
            Name  = record.Name,
        };

        // Compare per depot
        foreach (var (depotId, entry) in latest.Manifests)
        {
            if (!record.DepotManifestIds.TryGetValue(depotId, out ulong localMid))
            {
                // New depot not in local record — treat as update
                result.ChangedDepots[depotId] = (0, entry.ManifestId);
                result.UpdateAvailable = true;
                Logger.Debug($"  Depot {depotId}: NEW (remote {entry.ManifestId})");
                continue;
            }

            if (localMid != entry.ManifestId)
            {
                result.ChangedDepots[depotId] = (localMid, entry.ManifestId);
                result.UpdateAvailable = true;
                Logger.Debug($"  Depot {depotId}: changed {localMid} → {entry.ManifestId}");
            }
            else
            {
                Logger.Debug($"  Depot {depotId}: up to date ({localMid})");
            }
        }

        if (result.UpdateAvailable)
        {
            result.Message =
                $"Update available — {result.ChangedDepots.Count} depot(s) changed.";
            Logger.Info($"[{record.Name}] Update available: {result.ChangedDepots.Count} depot(s) changed");
        }
        else
        {
            result.Message = "Up to date.";
            Logger.Info($"[{record.Name}] Already up to date");
        }

        return result;
    }

    // ─── Check all downloaded games ───────────────────────────────────────────

    /// <summary>
    /// Check for updates across all games in the local database.
    /// </summary>
    public async Task<List<UpdateCheckResult>> CheckAllAsync()
    {
        var records = _db.GetAllGameRecords();
        if (records.Count == 0)
        {
            Logger.Warn("No downloaded games in local database.");
            return new List<UpdateCheckResult>();
        }

        Logger.Info($"Checking {records.Count} game(s) for updates...");

        var results = new List<UpdateCheckResult>();
        foreach (var rec in records)
        {
            try
            {
                var r = await CheckAsync(rec.AppId);
                results.Add(r);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Update check failed for {rec.Name}: {ex.Message}");
                results.Add(new UpdateCheckResult
                {
                    AppId   = rec.AppId,
                    Name    = rec.Name,
                    Message = $"Check failed: {ex.Message}",
                });
            }
        }

        int updatesAvailable = results.Count(r => r.UpdateAvailable);
        Logger.Info($"Update scan complete: {updatesAvailable}/{records.Count} game(s) have updates");
        return results;
    }

    // ─── Print update report ──────────────────────────────────────────────────

    public static void PrintReport(IEnumerable<UpdateCheckResult> results)
    {
        foreach (var r in results)
        {
            if (r.UpdateAvailable)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⬆  [{r.AppId}] {r.Name}  — {r.ChangedDepots.Count} depot(s) updated");
                foreach (var (depot, (local, remote)) in r.ChangedDepots)
                {
                    string localStr = local == 0 ? "(new)" : local.ToString();
                    Console.WriteLine($"       Depot {depot}: {localStr} → {remote}");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓  [{r.AppId}] {r.Name}  — up to date");
            }
            Console.ResetColor();
        }
    }
}
