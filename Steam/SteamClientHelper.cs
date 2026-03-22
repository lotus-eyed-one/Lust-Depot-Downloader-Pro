using Newtonsoft.Json.Linq;
using SteamKit2;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// Extended Steam client capabilities built on top of SteamSession.
///
/// This is the "custom Steam client" layer — it uses our existing CM connection
/// (anonymous or authenticated) plus the Steam Web API to extract manifests,
/// depot keys, and app info directly from Valve's servers without relying on
/// community sources.
///
/// Works in two modes:
///   Anonymous  — free/demo depots, public app info, package scanning
///   Logged in  — owned game depots, all keys, private branches
///
/// Pipeline when downloading (runs BEFORE community source fetch):
///   1. PICS query  → get all depot IDs + manifest IDs for the app
///   2. Web API     → enrich with package info, ownership data
///   3. Depot keys  → attempt anonymous key fetch first, auth fallback
///   4. Free package scan → check if any free packages expose this app
/// </summary>
public class SteamClientHelper
{
    private readonly SteamSession _steam;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public SteamClientHelper(SteamSession steam)
    {
        _steam = steam;
        string apiKey = EmbeddedConfig.SteamWebApiKey;
        if (!string.IsNullOrEmpty(apiKey))
            Logger.Debug("SteamClientHelper: Web API key loaded");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Full app scan — returns everything Steam knows about this app directly
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Query Steam directly (PICS + Web API) for all depot IDs, manifest IDs,
    /// and attempt to fetch depot keys for as many depots as possible.
    ///
    /// This runs BEFORE community sources and gives us the ground truth from
    /// Valve's servers. For games you own, this gives everything. For games
    /// you don't own, it gives manifest IDs (which community sources can then
    /// provide keys for) and sometimes keys for free/demo depots.
    /// </summary>
    public async Task<SteamAppInfo?> GetAppInfoAsync(uint appId, string branch = "public")
    {
        Logger.Debug($"[Steam] Querying app {appId} directly from Steam...");

        var info = new SteamAppInfo { AppId = appId };

        // ── 1. PICS product info ──────────────────────────────────────────
        try
        {
            var pics = await _steam.Apps.PICSGetProductInfo(
                new List<SteamApps.PICSRequest> { new(appId) },
                Enumerable.Empty<SteamApps.PICSRequest>());

            var appData = pics.Results?.FirstOrDefault()?.Apps;
            if (appData?.TryGetValue(appId, out var appInfo) == true)
            {
                info.Name = appInfo.KeyValues["common"]["name"].Value ?? $"App_{appId}";
                info.Type = appInfo.KeyValues["common"]["type"].Value ?? "";

                var depotsKv = appInfo.KeyValues["depots"];
                foreach (var depot in depotsKv.Children)
                {
                    if (!uint.TryParse(depot.Name, out uint depotId)) continue;

                    // Get manifest ID for the requested branch
                    ulong manifestId = 0;
                    foreach (var b in new[] { branch, "public" }.Distinct())
                    {
                        var gid = depot["manifests"][b]["gid"];
                        if (gid != KeyValue.Invalid &&
                            ulong.TryParse(gid.Value, out ulong mid))
                        {
                            manifestId = mid;
                            break;
                        }
                    }

                    // Extract depot metadata
                    var meta = new DepotMeta
                    {
                        DepotId    = depotId,
                        ManifestId = manifestId,
                        Name       = depot["name"].Value ?? $"Depot_{depotId}",
                        Os         = depot["config"]["oslist"].Value ?? "",
                        Arch       = depot["config"]["osarch"].Value ?? "",
                        Language   = depot["config"]["language"].Value ?? "",
                    };

                    info.Depots[depotId] = meta;
                }

                Logger.Debug($"[Steam] PICS: {info.Name}, {info.Depots.Count} depots");
            }
        }
        catch (Exception ex) { Logger.Debug($"[Steam] PICS query: {ex.Message}"); }

        // ── 2. Web API enrichment (app details, package list) ─────────────
        await EnrichWithWebApiAsync(info);

        // ── 3. Attempt depot key fetch for all discovered depots ──────────
        await FetchAllDepotKeysAsync(info, appId);

        // ── 4. Free package scan (may expose keys without ownership) ──────
        await ScanFreePackagesAsync(info, appId);

        return info.Depots.Count > 0 ? info : null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Steam Web API enrichment
    // ═══════════════════════════════════════════════════════════════════════

    private async Task EnrichWithWebApiAsync(SteamAppInfo info)
    {
        try
        {
            string apiKey = EmbeddedConfig.SteamWebApiKey;
            // App details from store API (works without a Web API key)
            var url = $"https://store.steampowered.com/api/appdetails?appids={info.AppId}&filters=basic,packages";
            var json = JObject.Parse(await _http.GetStringAsync(url));
            var data = json[info.AppId.ToString()]?["data"];
            if (data == null) return;

            info.Name = data["name"]?.ToString() ?? info.Name;
            info.IsFree = data["is_free"]?.Value<bool>() ?? false;

            // Package list — packages associated with this app
            // Free packages (package ID = 0 range) sometimes have accessible depot keys
            var packages = data["packages"];
            if (packages != null)
            {
                foreach (var pkg in packages)
                    if (uint.TryParse(pkg.ToString(), out uint pkgId))
                        info.PackageIds.Add(pkgId);

                Logger.Debug($"[WebAPI] {info.Name}: {info.PackageIds.Count} package(s), free={info.IsFree}");
            }
        }
        catch (Exception ex) { Logger.Debug($"[WebAPI] enrichment: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Depot key fetching — tries anonymous first, then auth
    // ═══════════════════════════════════════════════════════════════════════

    private async Task FetchAllDepotKeysAsync(SteamAppInfo info, uint appId)
    {
        if (info.Depots.Count == 0) return;

        // Fire all depot key requests in parallel
        var tasks = info.Depots.Keys.Select(async depotId =>
        {
            var key = await _steam.GetDepotKeyAsync(depotId, appId);
            if (key != null)
            {
                info.Depots[depotId].DepotKey = key;
                Logger.Debug($"[Steam] Depot key {depotId}: fetched from Steam");
            }
        });

        await Task.WhenAll(tasks);

        int keysFound = info.Depots.Values.Count(d => d.DepotKey != null);
        if (keysFound > 0)
            Logger.Info($"[Steam] {keysFound}/{info.Depots.Count} depot key(s) fetched directly from Steam");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Free package scan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Steam has "free" packages (package 0 is always free, others are free weekends,
    /// F2P packages, demo packages). Anonymous sessions can sometimes get depot keys
    /// for depots in these packages without owning the full game.
    ///
    /// This is especially useful for:
    /// - Demo depots (the game demo is always free)
    /// - F2P portions of partially-free games
    /// - Redistributable packages (DirectX, Visual C++, etc.)
    /// </summary>
    private async Task ScanFreePackagesAsync(SteamAppInfo info, uint appId)
    {
        // Skip if we already have all keys
        if (info.Depots.Values.All(d => d.DepotKey != null)) return;

        try
        {
            // Package 0 is the free-to-play/demo package on Steam
            // Request package info for package 0 to see if this app is included
            var packages = new List<uint> { 0 };

            // Add any known free packages for this app (from Web API enrichment)
            packages.AddRange(info.PackageIds.Where(id => id < 100)); // low IDs tend to be free packages

            var pkgResult = await _steam.Apps.PICSGetProductInfo(
                Enumerable.Empty<SteamApps.PICSRequest>(),
                packages.Distinct().Select(id => new SteamApps.PICSRequest(id)));

            if (pkgResult.Results == null) return;

            foreach (var result in pkgResult.Results)
            {
                foreach (var (pkgId, pkgInfo) in result.Packages ?? new())
                {
                    // Check if this package contains our appId
                    var appIds = pkgInfo.KeyValues["appids"].Children;
                    if (!appIds.Any(a => a.Value == appId.ToString())) continue;

                    // Try to get keys for depots in this package
                    var pkgDepots = pkgInfo.KeyValues["depotids"].Children;
                    foreach (var d in pkgDepots)
                    {
                        if (!uint.TryParse(d.Value, out uint depotId)) continue;
                        if (!info.Depots.ContainsKey(depotId)) continue;
                        if (info.Depots[depotId].DepotKey != null) continue;

                        var key = await _steam.GetDepotKeyAsync(depotId, appId);
                        if (key != null)
                        {
                            info.Depots[depotId].DepotKey = key;
                            Logger.Debug($"[Steam] Free package {pkgId} exposed depot key {depotId}");
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Logger.Debug($"[Steam] Free package scan: {ex.Message}"); }
    }
}

// ─── Data models ──────────────────────────────────────────────────────────────

public class SteamAppInfo
{
    public uint   AppId      { get; set; }
    public string Name       { get; set; } = "";
    public string Type       { get; set; } = "";
    public bool   IsFree     { get; set; }

    public Dictionary<uint, DepotMeta> Depots     { get; set; } = new();
    public HashSet<uint>               PackageIds  { get; set; } = new();
}

public class DepotMeta
{
    public uint    DepotId    { get; set; }
    public ulong   ManifestId { get; set; }
    public string  Name       { get; set; } = "";
    public string  Os         { get; set; } = "";
    public string  Arch       { get; set; } = "";
    public string  Language   { get; set; } = "";
    public byte[]? DepotKey   { get; set; }
}
