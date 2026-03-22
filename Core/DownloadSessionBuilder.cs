using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Steam;
using LustsDepotDownloaderPro.Utils;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace LustsDepotDownloaderPro.Core;

public class DownloadSessionBuilder
{
    private readonly SteamSession      _steam;
    private readonly DownloadOptions   _options;
    private readonly SteamClientHelper _steamHelper;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public DownloadSessionBuilder(SteamSession steam, DownloadOptions options)
    {
        _steam       = steam;
        _options     = options;
        _steamHelper = new SteamClientHelper(steam);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LustsDepotDownloaderPro/1.0");
    }

    // ─── App name ────────────────────────────────────────────────────────

    public async Task<string> GetAppNameAsync(uint appId)
    {
        try
        {
            var body = await _http.GetStringAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic");
            var name = JObject.Parse(body)[appId.ToString()]?["data"]?["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception ex) { Logger.Debug($"GetAppName: {ex.Message}"); }
        return $"App_{appId}";
    }

    // ─── Workshop download ────────────────────────────────────────────────

    public async Task<DownloadSession> BuildWorkshopSessionAsync(string outputPath)
    {
        var session = new DownloadSession
        {
            AppId          = _options.AppId,
            AppName        = await GetAppNameAsync(_options.AppId),
            OutputDir      = outputPath,
            // SessionKey set by engine
            MaxDownloads   = _options.MaxDownloads,
            FallbackWorkers = _options.FallbackWorkers,
        };

        // Resolve workshop item → (depotId, manifestId) via SteamWorkshop
        try
        {
            if (_options.PubFileId.HasValue)
            {
                Logger.Warn($"Workshop downloads (PublishedFileId) are not yet fully implemented in this version.");
                Logger.Info("Workshop download requires complex Web API calls. Falling back to standard app download...");
                // TODO: Implement using ISteamRemoteStorage/GetPublishedFileDetails Web API
                // or SteamWorkshop handler
            }
            else if (_options.UgcId.HasValue)
            {
                Logger.Warn($"Workshop UGC downloads are not yet fully implemented in this version.");
                Logger.Info("Falling back to standard app download...");
                // TODO: Implement UGC download
            }

            // Fall back to building a normal session for the parent app
            return await BuildAsync(outputPath);
        }
        catch (Exception ex)
        {
            Logger.Error($"Workshop session build failed: {ex.Message}");
            throw;
        }
    }

    // ─── Main build ──────────────────────────────────────────────────────

    public async Task<DownloadSession> BuildAsync(string outputPath)
    {
        Logger.Debug($"Building session for AppID {_options.AppId}");

        var session = new DownloadSession
        {
            AppId             = _options.AppId,
            AppName           = await GetAppNameAsync(_options.AppId),
            OutputDir         = outputPath,
            // SessionKey is set by DownloadEngine after construction
            MaxDownloads      = _options.MaxDownloads,
            FallbackWorkers   = _options.FallbackWorkers,
            ValidateChecksums = _options.Validate
        };

        if (!string.IsNullOrEmpty(_options.FileListPath))
            session.FileFilters = FileUtils.LoadFileList(_options.FileListPath).ToHashSet();

        var userDepotKeys = LoadDepotKeys(_options.DepotKeysFile);

        // ── Race: Steam direct query + community sources in parallel ──────────────
        // Both run simultaneously. First source to return useful data wins.
        // Total cap: 12 seconds. If either times out, we use whatever the other got.
        Logger.Debug("Racing Steam direct query vs community sources...");

        var allTokens = new[] { _options.ApiKey }
            .Concat(EmbeddedConfig.GitHubTokens)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct()
            .ToArray();

        using var raceCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var steamTask = Task.Run(async () =>
        {
            try
            {
                return await _steamHelper.GetAppInfoAsync(
                    _options.AppId, _options.Branch ?? "public");
            }
            catch { return null; }
        }, raceCts.Token);

        var fetcher        = new ManifestSourceFetcher(allTokens);
        var communityTask  = Task.Run(async () =>
        {
            try { return await fetcher.FetchAsync(_options.AppId); }
            catch { return null; }
        }, raceCts.Token);

        // Wait for both (bounded by the 20s cap)
        await Task.WhenAll(
            steamTask.ContinueWith(_ => {}),
            communityTask.ContinueWith(_ => {}));

        SteamAppInfo?   steamInfo      = steamTask.IsCompletedSuccessfully     ? steamTask.Result     : null;
        ManifestResult? communityResult = communityTask.IsCompletedSuccessfully ? communityTask.Result : null;

        if (steamInfo != null)
        {
            int ks = steamInfo.Depots.Values.Count(d => d.DepotKey   != null);
            int ms = steamInfo.Depots.Values.Count(d => d.ManifestId >  0);
            if (ms > 0 || ks > 0)
                Logger.Info($"[Steam] Direct: {ms} manifest(s), {ks} key(s)");
        }

        // Populate build ID from Steam PICS data
        try
        {
            var picsResult = await _steam.Apps.PICSGetProductInfo(
                new List<SteamKit2.SteamApps.PICSRequest>
                    { new SteamKit2.SteamApps.PICSRequest(_options.AppId) },
                Enumerable.Empty<SteamKit2.SteamApps.PICSRequest>());
            var appData = picsResult.Results?.FirstOrDefault()?.Apps;
            if (appData?.TryGetValue(_options.AppId, out var info) == true)
            {
                var buildKv = info.KeyValues["depots"]["branches"][_options.Branch ?? "public"]["buildid"];
                if (buildKv != SteamKit2.KeyValue.Invalid &&
                    uint.TryParse(buildKv.Value, out uint bid))
                    session.BuildId = bid;
            }
        }
        catch (Exception ex) { Logger.Debug($"BuildId fetch: {ex.Message}"); }

        // Depot list from PICS
        List<uint> picsDepotIds;
        if (_options.DepotId.HasValue)
            picsDepotIds = new List<uint> { _options.DepotId.Value };
        else
            picsDepotIds = await GetDepotIdsForAppAsync(_options.AppId);

        // Merge PICS + community depot IDs
        var allDepotIds = new HashSet<uint>(picsDepotIds);
        if (communityResult != null)
        {
            int added = 0;
            foreach (var id in communityResult.Manifests.Keys)
                if (allDepotIds.Add(id)) added++;
            if (added > 0) Logger.Debug($"Added {added} community-only depot(s)");
        }

        if (allDepotIds.Count == 0)
            throw new Exception(
                "No depots found. Try --depot <id> --manifest <id> explicitly.");

        Logger.Debug($"Depots to process: {allDepotIds.Count}");

        // If manifest-only, just report and return empty session
        if (_options.ManifestOnly)
        {
            Logger.Info("=== MANIFEST-ONLY MODE ===");
            foreach (var depotId in allDepotIds)
            {
                ulong mid = 0;
                if (communityResult?.Manifests.TryGetValue(depotId, out var cm) == true)
                    mid = cm.ManifestId;
                if (mid == 0) mid = await GetManifestIdFromPicsAsync(_options.AppId, depotId) ?? 0;
                Logger.Info($"  Depot {depotId} → manifest {(mid == 0 ? "(not found)" : mid.ToString())}");
            }
            return session; // empty, no depots added
        }

        // Prepare each depot
        foreach (var depotId in allDepotIds)
        {
            var depot = await PrepareDepotAsync(
                _options.AppId, depotId, _options.ManifestId,
                userDepotKeys, communityResult, steamInfo);
            if (depot != null) session.Depots.Add(depot);
        }

        if (session.Depots.Count == 0)
            throw new Exception(
                $"No downloadable depots found from {allDepotIds.Count} tried. " +
                "For paid games, try --username/--password or ensure community sources have it.");

        Logger.Info($"Session ready: {session.Depots.Count}/{allDepotIds.Count} depot(s)");
        return session;
    }

    // ─── Single depot ─────────────────────────────────────────────────────

    private async Task<DepotInfo?> PrepareDepotAsync(
        uint appId, uint depotId, ulong? forcedManifestId,
        Dictionary<uint, byte[]> userDepotKeys, ManifestResult? communityResult,
        SteamAppInfo? steamInfo = null)
    {
        try
        {
            Logger.Debug($"Preparing depot {depotId}");

            // 1. Depot key — priority: user file > Steam direct > community > Steam API
            byte[]? depotKey = null;
            if (userDepotKeys.TryGetValue(depotId, out var uk))
            { depotKey = uk; Logger.Debug($"Depot {depotId}: user-provided key"); }
            else if (steamInfo?.Depots.TryGetValue(depotId, out var sd) == true && sd.DepotKey != null)
            { depotKey = sd.DepotKey; Logger.Debug($"Depot {depotId}: key from Steam direct"); }
            else if (communityResult?.DepotKeys.TryGetValue(depotId, out var ck) == true)
            { depotKey = ck; Logger.Debug($"Depot {depotId}: community key"); }
            else
            {
                depotKey = await _steam.GetDepotKeyAsync(depotId, appId);
                if (depotKey == null)
                    Logger.Debug($"Depot {depotId}: no key (free/anon depot or access denied)");
            }

            // 2a. Local manifest file override
            if (!string.IsNullOrEmpty(_options.ManifestFile) && File.Exists(_options.ManifestFile))
            {
                var local = ManifestParser.LoadFromFile(_options.ManifestFile, depotKey);
                if (local != null)
                    return new DepotInfo
                    { DepotId = depotId, DepotName = $"Depot_{depotId}",
                      DepotKey = depotKey, ManifestId = 0, Files = local };
            }

            // 2b. Community binary manifest
            if (communityResult?.Manifests.TryGetValue(depotId, out var cm) == true && cm.Data != null)
            {
                Logger.Debug($"Depot {depotId}: using community manifest binary ({cm.Data.Length:N0} bytes)");
                try
                {
                    var dm = SteamKit2.DepotManifest.Deserialize(cm.Data);
                    if (depotKey != null) dm.DecryptFilenames(depotKey);
                    return new DepotInfo
                    { DepotId = depotId, DepotName = $"Depot_{depotId}",
                      DepotKey = depotKey, ManifestId = cm.ManifestId,
                      Files = ManifestParser.FromDepotManifest(dm) };
                }
                catch (Exception ex)
                { Logger.Warn($"Depot {depotId}: community manifest parse failed ({ex.Message}) — CDN fallback"); }
            }

            // 2c. Resolve manifest ID
            // Priority: explicit arg > community > Steam direct (PICS cached) > PICS live query
            ulong manifestId = forcedManifestId ?? 0;
            if (manifestId == 0 && communityResult?.Manifests.TryGetValue(depotId, out var cme) == true)
                manifestId = cme.ManifestId;
            if (manifestId == 0 && steamInfo?.Depots.TryGetValue(depotId, out var sm) == true)
                manifestId = sm.ManifestId;
            if (manifestId == 0)
                manifestId = await GetManifestIdFromPicsAsync(appId, depotId) ?? 0;

            if (manifestId == 0)
            {
                Logger.Warn($"Depot {depotId}: no manifest ID — skipping");
                return null;
            }

            // 2d. Try fetching manifest binary directly from community CDN mirrors
            // This works anonymously — the manifest file itself doesn't need a request code,
            // only chunk downloads do. We try all known repos that had this depotId.
            var communityBinary = await TryFetchManifestBinaryAsync(
                appId, depotId, manifestId, communityResult);
            if (communityBinary != null)
            {
                try
                {
                    var dm = SteamKit2.DepotManifest.Deserialize(communityBinary);
                    if (depotKey != null) dm.DecryptFilenames(depotKey);
                    Logger.Debug($"Depot {depotId}: manifest fetched from community CDN");
                    return new DepotInfo
                    { DepotId = depotId, DepotName = $"Depot_{depotId}",
                      DepotKey = depotKey, ManifestId = manifestId,
                      Files = ManifestParser.FromDepotManifest(dm) };
                }
                catch (Exception ex)
                { Logger.Debug($"Depot {depotId}: community binary parse failed: {ex.Message}"); }
            }

            // 2e. CDN manifest via Steam (requires auth for paid games)
            Logger.Debug($"Depot {depotId}: trying Steam CDN for manifest {manifestId}...");
            var manifest = await _steam.DownloadManifestAsync(
                appId, depotId, manifestId, depotKey,
                _options.Branch ?? "public", _options.BranchPassword);

            if (manifest == null)
            {
                Logger.Error($"Depot {depotId}: manifest not available. " +
                             "The game may not be in community sources. " +
                             "Use --username/--password if you own it.");
                return null;
            }

            return new DepotInfo
            { DepotId = depotId, DepotName = $"Depot_{depotId}",
              DepotKey = depotKey, ManifestId = manifestId,
              Files = ManifestParser.FromDepotManifest(manifest) };
        }
        catch (Exception ex)
        {
            Logger.Error($"Depot {depotId} failed: {ex.Message}");
            return null;
        }
    }

    // ─── Community manifest binary fetch ─────────────────────────────────────

    private static readonly string[] CdnMirrors =
    {
        "https://raw.githubusercontent.com/{0}/{1}/{2}",
        "https://raw.gitmirror.com/{0}/{1}/{2}",
        "https://ghfast.top/https://raw.githubusercontent.com/{0}/{1}/{2}",
        "https://cdn.jsdmirror.com/gh/{0}@{1}/{2}",
        "https://raw.dgithub.xyz/{0}/{1}/{2}",
        "https://gh.akass.cn/{0}/{1}/{2}",
    };

    private static readonly (string Repo, string Branch)[] ManifestRepos =
    {
        ("ikun0014/ManifestHub",                ""),
        ("SteamAutoCracks/ManifestHub",         ""),
        ("Auiowu/ManifestAutoUpdate",           ""),
        ("tymolu233/ManifestAutoUpdate",        ""),
        ("BlankTMing/ManifestAutoUpdate",       ""),
        ("ikunshare/ManifestHub",               ""),
        ("wxy1343/ManifestAutoUpdate",          ""),
        ("kimycai/ManifestHub.2025.7.24",       "main"),
    };

    /// <summary>
    /// Try to fetch a manifest binary directly from GitHub CDN mirrors.
    /// Works anonymously — no Steam auth needed.
    /// The file is stored as {depotId}_{manifestId}.manifest on the branch named {appId}.
    /// </summary>
    private async Task<byte[]?> TryFetchManifestBinaryAsync(
        uint appId, uint depotId, ulong manifestId, ManifestResult? communityResult)
    {
        string fileName = $"{depotId}_{manifestId}.manifest";

        // If community result has a SHA for the branch, use it directly
        if (communityResult?.Manifests.TryGetValue(depotId, out var cm) == true
            && cm.BranchSha != null)
        {
            foreach (var mirror in CdnMirrors)
            {
                try
                {
                    string url = string.Format(mirror, cm.Repo ?? "ikun0014/ManifestHub",
                        cm.BranchSha, fileName);
                    using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var resp = await _http.GetAsync(url, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var data = await resp.Content.ReadAsByteArrayAsync();
                        if (data.Length > 10) return data;
                    }
                }
                catch { }
            }
        }

        // Try all known repos using appId as branch name
        var tasks = ManifestRepos.Select(async r =>
        {
            string branch = string.IsNullOrEmpty(r.Branch) ? appId.ToString() : r.Branch;
            foreach (var mirror in CdnMirrors.Take(3)) // top 3 mirrors only for speed
            {
                try
                {
                    // Path format: {depotId}_{manifestId}.manifest on branch {appId}
                    // Raw URL: raw.githubusercontent.com/{repo}/{branch}/{fileName}
                    string url = string.Format(mirror, r.Repo, branch, fileName);
                    using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var resp = await _http.GetAsync(url, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var data = await resp.Content.ReadAsByteArrayAsync();
                        if (data.Length > 10) return data;
                    }
                }
                catch { }
            }
            return (byte[]?)null;
        });

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r != null);
    }

        // ─── PICS helpers ─────────────────────────────────────────────────────

    private async Task<ulong?> GetManifestIdFromPicsAsync(uint appId, uint depotId)
    {
        try
        {
            var result = await _steam.Apps.PICSGetProductInfo(
                new List<SteamApps.PICSRequest> { new SteamApps.PICSRequest(appId) },
                Enumerable.Empty<SteamApps.PICSRequest>());

            var apps = result.Results?.FirstOrDefault()?.Apps;
            if (apps == null || !apps.TryGetValue(appId, out var info)) return null;

            var depotKv = info.KeyValues["depots"][depotId.ToString()];
            if (depotKv == KeyValue.Invalid) return null;

            foreach (var branch in new[] { _options.Branch ?? "public", "public" }.Distinct())
            {
                var gid = depotKv["manifests"][branch]["gid"];
                if (gid != KeyValue.Invalid &&
                    !string.IsNullOrEmpty(gid.Value) &&
                    ulong.TryParse(gid.Value, out ulong mid))
                {
                    Logger.Info($"PICS depot {depotId} branch '{branch}' → manifest {mid}");
                    return mid;
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"PICS manifest lookup depot {depotId}: {ex.Message}"); }
        return null;
    }

    private async Task<List<uint>> GetDepotIdsForAppAsync(uint appId)
    {
        var ids = new List<uint>();
        try
        {
            var result = await _steam.Apps.PICSGetProductInfo(
                new List<SteamApps.PICSRequest> { new SteamApps.PICSRequest(appId) },
                Enumerable.Empty<SteamApps.PICSRequest>());

            var apps = result.Results?.FirstOrDefault()?.Apps;
            if (apps == null || !apps.TryGetValue(appId, out var info)) return ids;

            foreach (var depot in info.KeyValues["depots"].Children)
            {
                if (!uint.TryParse(depot.Name, out uint depotId)) continue;

                if (!_options.AllPlatforms)
                {
                    var os = depot["config"]["oslist"].Value;
                    if (!string.IsNullOrEmpty(os) &&
                        !os.Contains(_options.Os, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (!_options.AllArchs)
                {
                    var arch = depot["config"]["osarch"].Value;
                    if (!string.IsNullOrEmpty(arch) && arch != _options.OsArch) continue;
                }
                if (!_options.AllLanguages)
                {
                    var lang = depot["config"]["language"].Value;
                    if (!string.IsNullOrEmpty(lang) &&
                        !lang.Equals(_options.Language, StringComparison.OrdinalIgnoreCase) &&
                        !lang.Equals("english", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (_options.LowViolence)
                {
                    var lv = depot["config"]["lowviolence"].Value;
                    if (string.IsNullOrEmpty(lv) || lv != "1") continue;
                }
                ids.Add(depotId);
            }
            Logger.Debug($"PICS: {ids.Count} depot(s) for app {appId}");
        }
        catch (Exception ex) { Logger.Warn($"GetDepotIds: {ex.Message}"); }
        return ids;
    }

    // ─── Depot key file ────────────────────────────────────────────────────

    private static Dictionary<uint, byte[]> LoadDepotKeys(string? path)
    {
        var keys = new Dictionary<uint, byte[]>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return keys;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                var parts = line.Split(new[] { ';', '=', '\t', ' ' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !uint.TryParse(parts[0], out var id)) continue;
                keys[id] = Convert.FromHexString(parts[1].Replace(" ", "").Replace("-", ""));
            }
            Logger.Info($"Loaded {keys.Count} depot key(s) from file");
        }
        catch (Exception ex) { Logger.Warn($"LoadDepotKeys: {ex.Message}"); }
        return keys;
    }
}
