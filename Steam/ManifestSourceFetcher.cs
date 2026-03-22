using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LustsDepotDownloaderPro.Utils;
using Newtonsoft.Json.Linq;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// Fetches manifests and depot keys from every known community source.
/// All sources run in parallel; results are merged — a game whose keys live
/// in repo #5 and whose manifest binary is in repo #18 will get both.
///
/// ══════════════════════════════════════════════════════════════════
///  GitHub branch-type sources (per-AppID branch, Key.vdf + .manifest)
/// ══════════════════════════════════════════════════════════════════
///   1.  ikun0014/ManifestHub
///   2.  Auiowu/ManifestAutoUpdate
///   3.  tymolu233/ManifestAutoUpdate
///   4.  SteamAutoCracks/ManifestHub
///   5.  sean-who/ManifestAutoUpdate          (XOR-encrypted Key.vdf)
///   6.  BlankTMing/ManifestAutoUpdate         ★ original by the BlankTMing
///   7.  wxy1343/ManifestAutoUpdate
///   8.  pjy612/SteamManifestCache            ★ 1.5k stars — manifests only
///   9.  nicklvsa/ManifestAutoUpdate
///  10.  P-ToyStore/SteamManifestCache_Pro
///  11.  isKoi/ManifestAutoUpdate
///  12.  yunxiao6/ManifestAutoUpdate
///  13.  BlueAmulet/ManifestAutoUpdate
///  14.  nicholasess/ManifestAutoUpdate
///  15.  masqueraigne/ManifestAutoUpdate
///  16.  WoodenTiger000/SteamManifestHub
///  17.  TheSecondComing001/SteamManifestHub
///  18.  eudaimence/OpenDepot
///  19.  ikunshare/ManifestHub
///  20.  Onekey-Project/Manifest-AutoUpdate
///  21.  SteamManifestHub/ManifestHub           (archive mirror)
///  22.  forcesteam/ManifestAutoUpdate
///  23.  Egsagon/ManifestAutoUpdate
///  24.  itsnotlupus/ManifestAutoUpdate
///  25.  zxcv3000/ManifestAutoUpdate
///  26.  r0ck3tz/ManifestAutoUpdate
///  27.  AlexIsTheGuy/ManifestAutoUpdate
///  28.  SteamContentLeak/ManifestAutoUpdate
///  29.  Kiraio-lgtm/ManifestAutoUpdate
///  30.  DreamSourceLab/ManifestAutoUpdate
///
/// ══════════════════════════════════════════════════════════════════
///  Special encrypted source
/// ══════════════════════════════════════════════════════════════════
///  31.  luckygametools/steam-cfg              (AES+XOR+gob .dat)
///
/// ══════════════════════════════════════════════════════════════════
///  REST / ZIP sources
/// ══════════════════════════════════════════════════════════════════
///  32.  printedwaste.com
///  33.  steambox.gdata.fun
///  34.  cysaw.top
///  35.  manifesthub1.filegear-sg.me  (REST API — needs MANIFESTHUB_API_KEY in .env)
///  36.  depotbox.org               (REST API — needs DEPOTBOX_API_KEY in .env)
///  37.  manifest.youngzm.com        (no key needed)
///
/// ══════════════════════════════════════════════════════════════════
///  SteamTools Lua sources (depot keys in Lua format)
/// ══════════════════════════════════════════════════════════════════
///
///  Lua sources (no API key needed for any of these):
///  38.  openlua.cloud / api.openlua.cloud    (formerly Manifestor.cc)
///  39.  steamml.vercel.app
///  40.  steamtools.pages.dev
///  41.  walftech.com                  (also has DLC data)
///  42.  steammanifest.com             (Walftech division, diff database)
///  43.  steamtools.site
///  44.  manifestlua.blog
///  45.  ssmg4.github.io
///  46.  manifest.youngzm.com
/// </summary>
public class ManifestSourceFetcher
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        AllowAutoRedirect = true
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    // Dual-token pool — each token has its own 5000 req/hr limit
    // Requests round-robin across all available tokens
    private readonly string[] _githubTokens;
    private int _tokenIndex = -1;              // starts at -1 so first Increment gives 0
    private readonly bool[] _tokenRateLimited; // per-token rate limit flag

    // XOR key used by sean-who/ManifestAutoUpdate for Key.vdf
    private static readonly byte[] SeanWhoXorKey =
        Encoding.UTF8.GetBytes("Scalping dogs, I'll fuck you");

    // AES key used by luckygametools
    private static readonly byte[] LuckyAesKey =
        Encoding.UTF8.GetBytes(" s  t  e  a  m  ");

    // XOR key used by luckygametools after AES
    private static readonly byte[] LuckyXorKey = Encoding.UTF8.GetBytes("hail");

    // Per-token rate-limit tracking
    // (global flag checked for backward compat; per-token for dual mode)
    private volatile int _anyTokenRateLimited = 0;

    // ─── Source table ─────────────────────────────────────────────────────
    // (repo, xorKeyForKeyVdf)   null = plain Key.vdf, no extra encryption

    private static readonly (string Repo, byte[]? XorKey)[] GitHubSources =
    {
        // ── Tier 1: original / highest-coverage repos ─────────────────────
        ("ikun0014/ManifestHub",                null),
        ("Auiowu/ManifestAutoUpdate",           null),
        ("tymolu233/ManifestAutoUpdate",        null),
        ("SteamAutoCracks/ManifestHub",         null),
        ("sean-who/ManifestAutoUpdate",         SeanWhoXorKey),

        // ── Tier 2: high-star, actively maintained ───────────────────────
        ("BlankTMing/ManifestAutoUpdate",       null),   // ★405 — the OG
        ("wxy1343/ManifestAutoUpdate",          null),   // ★ referenced by oureveryday tools

        // ── Tier 3: broad community forks / mirrors ──────────────────────
        ("nicklvsa/ManifestAutoUpdate",         null),
        ("isKoi/ManifestAutoUpdate",            null),
        ("yunxiao6/ManifestAutoUpdate",         null),
        ("BlueAmulet/ManifestAutoUpdate",       null),
        ("nicholasess/ManifestAutoUpdate",      null),
        ("masqueraigne/ManifestAutoUpdate",     null),
        ("WoodenTiger000/SteamManifestHub",     null),   // mirror of SteamAutoCracks
        ("TheSecondComing001/SteamManifestHub", null),   // mirror of SteamAutoCracks
        ("eudaimence/OpenDepot",                null),
        ("ikunshare/ManifestHub",               null),   // ikun's own hub
        ("Onekey-Project/Manifest-AutoUpdate",  null),   // referenced by Onekey forks

        // ── Tier 4: additional community repos ──────────────────────────
        ("forcesteam/ManifestAutoUpdate",       null),
        ("Egsagon/ManifestAutoUpdate",          null),
        ("itsnotlupus/ManifestAutoUpdate",      null),
        ("zxcv3000/ManifestAutoUpdate",         null),
        ("r0ck3tz/ManifestAutoUpdate",          null),
        ("AlexIsTheGuy/ManifestAutoUpdate",     null),
        ("SteamContentLeak/ManifestAutoUpdate", null),
        ("Kiraio-lgtm/ManifestAutoUpdate",      null),
        ("DreamSourceLab/ManifestAutoUpdate",   null),
    };

    /// <param name="githubTokens">
    /// One or more GitHub tokens. If multiple are provided, requests are
    /// round-robined across them — each token has its own 5000 req/hr limit,
    /// so two tokens = 10,000 req/hr effective capacity.
    /// </param>
    public ManifestSourceFetcher(params string?[] githubTokens)
    {
        // Filter out null/empty, deduplicate
        _githubTokens = githubTokens
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct()
            .ToArray();
        _tokenRateLimited = new bool[Math.Max(1, _githubTokens.Length)];
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LustsDepotDownloaderPro/1.0");

        if (_githubTokens.Length > 0)
            Logger.Debug($"GitHub: {_githubTokens.Length} token(s) loaded " +
                         $"({_githubTokens.Length * 5000:N0} req/hr capacity)");
        else
            Logger.Debug("GitHub: no tokens — anonymous (60 req/hr). Add tokens to .env.");
    }

    // ─── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Fetch from ALL 34 sources in parallel and merge results.
    /// </summary>
    public async Task<ManifestResult?> FetchAsync(uint appId)
    {
        Logger.Info($"Searching ALL community manifest sources for app {appId}...");
        _anyTokenRateLimited = 0;
        for (int i = 0; i < _tokenRateLimited.Length; i++) _tokenRateLimited[i] = false;

        var merged = new ManifestResult { AppId = appId, Source = "merged" };

        // All GitHub repos + luckygametools + REST sources — fully parallel
        var githubTasks = GitHubSources
            .Select(s => TryGitHubBranchAsync(appId, s.Repo, s.XorKey))
            .ToList();

        var luckyTask = TryLuckyGameToolsAsync(appId);

        var restTasks = new List<Task<ManifestResult?>>
        {
            TryPrintedWasteAsync(appId),
            TryGdataAsync(appId),
            TryCysawAsync(appId),
            TryManifestHubApiAsync(appId),   // api.manifesthub1.filegear-sg.me (needs API key in .env)
            TryDepotBoxAsync(appId),          // depotbox.org (needs API key in .env)
        };

        var pjy612Task   = TryPjy612TagReposAsync(appId);
        var luaTask      = TryLuaSourcesAsync(appId);

        var allTasks = githubTasks
            .Concat(new[] { luckyTask, pjy612Task, luaTask })
            .Concat(restTasks);

        ManifestResult?[] results;
        try
        {
            results = await Task.WhenAll(allTasks);
        }
        catch
        {
            results = allTasks.Select(t => t.IsCompletedSuccessfully ? t.Result : null).ToArray();
        }

        int newManifests = 0, newKeys = 0;
        foreach (var r in results)
        {
            if (r == null || (r.Manifests.Count == 0 && r.DepotKeys.Count == 0)) continue;
            int mBefore = merged.Manifests.Count, kBefore = merged.DepotKeys.Count;
            MergeInto(merged, r);
            int mAdded = merged.Manifests.Count - mBefore;
            int kAdded = merged.DepotKeys.Count - kBefore;
            newManifests += mAdded; newKeys += kAdded;
            if (mAdded > 0 || kAdded > 0)
                Logger.Info($"[{r.Source}] +{mAdded} manifest(s), +{kAdded} key(s)");
        }

        if (_anyTokenRateLimited == 1)
            Logger.Warn("GitHub API rate limit hit. Use --api-key <github_pat> " +
                        "to raise cap from 60 to 5000 req/hr.");

        if (merged.Manifests.Count == 0 && merged.DepotKeys.Count == 0)
        {
            Logger.Warn($"No community data found for app {appId}. " +
                        "The game may not be in any community repo yet, " +
                        "or GitHub rate limit was reached (use --api-key).");
            return null;
        }

        Logger.Info($"Total from all sources: {merged.Manifests.Count} depot(s), " +
                    $"{merged.DepotKeys.Count} key(s)");
        return merged;
    }

    // ─── Merge helper ─────────────────────────────────────────────────────

    private static void MergeInto(ManifestResult target, ManifestResult source)
    {
        foreach (var (id, entry) in source.Manifests)
        {
            if (!target.Manifests.TryGetValue(id, out var existing))
                target.Manifests[id] = entry;
            else if (existing.Data == null && entry.Data != null)
                target.Manifests[id] = entry;   // upgrade ID-only → binary
        }
        foreach (var (id, key) in source.DepotKeys)
            if (!target.DepotKeys.ContainsKey(id))
                target.DepotKeys[id] = key;
    }

    // ─── pjy612/SteamManifestCache + forks — Tag-based lookup ───────────────
    // pjy612 uses Git TAGS instead of branches. Format: manifest_{AppId}_{sha}.manifest
    // Query: GET /repos/{repo}/git/refs/tags/manifest_{appId}  (or list all tags)
    // This is a fundamentally different structure from the ManifestAutoUpdate branch format.
    // Archived Jan 2025 but still has ~60k manifests. X06W90X/pjy612 is the active fork.

    private static readonly string[] Pjy612StyleRepos =
    {
        "X06W90X/pjy612",          // active fork of pjy612, updated Dec 2025
        "pjy612/SteamManifestCache", // archived Jan 2025 but 1.5k stars — huge dataset
    };

    private async Task<ManifestResult?> TryPjy612TagReposAsync(uint appId)
    {
        // Fire all pjy612-style repos in parallel and merge
        var tasks = Pjy612StyleRepos
            .Select(repo => TryPjy612TagRepoAsync(appId, repo))
            .ToList();
        var results = await Task.WhenAll(tasks);

        var merged = new ManifestResult { AppId = appId, Source = "pjy612-style" };
        foreach (var r in results.Where(r => r != null))
            MergeInto(merged, r!);
        return merged.Manifests.Count > 0 || merged.DepotKeys.Count > 0 ? merged : null;
    }

    private async Task<ManifestResult?> TryPjy612TagRepoAsync(uint appId, string repo)
    {
        try
        {
            Logger.Debug($"[{repo}] checking tags for app {appId}...");
            var (headers, tokenIdx) = BuildGitHubHeaders();

            // List tags matching this appId pattern
            // pjy612 tag format: manifest_{AppId}_{depotId}_{manifestId}.manifest
            var tagsUrl  = $"https://api.github.com/repos/{repo}/git/refs/tags/manifest_{appId}";
            var tagsJson = await FetchJsonArrayAsync(tagsUrl, headers, tokenIdx);

            // Fallback: check the branch list too (some forks added branch support)
            if (tagsJson == null || tagsJson.Count == 0)
            {
                var branchUrl  = $"https://api.github.com/repos/{repo}/branches/{appId}";
                var branchJson = await FetchJsonAsync(branchUrl, headers, tokenIdx);
                if (branchJson != null && branchJson.ContainsKey("commit"))
                    return await TryGitHubBranchAsync(appId, repo, null);
                return null;
            }

            var result = new ManifestResult { AppId = appId, Source = repo };

            foreach (var tag in tagsJson)
            {
                // Tag ref: refs/tags/manifest_{AppId}_{depotId}_{manifestId}.manifest
                string? refStr = tag["ref"]?.ToString();
                if (refStr == null) continue;

                string tagName = refStr.Replace("refs/tags/", "");
                // Parse: manifest_{AppId}_{depotId}_{manifestId}.manifest
                var parts = tagName.Replace("manifest_", "").Replace(".manifest", "").Split('_');
                if (parts.Length < 3) continue;
                if (!uint.TryParse(parts[1], out uint depotId)) continue;
                if (!ulong.TryParse(parts[2], out ulong manifestId)) continue;

                // Get the SHA for this tag to fetch the blob
                string? sha = tag["object"]?["sha"]?.ToString();
                if (sha == null) continue;

                // Fetch the actual manifest file from the tag
                byte[]? data = await FetchRawAsync(sha, $"{tagName}", repo);

                // Record the manifest (data may be null — we still record the ID)
                if (!result.Manifests.ContainsKey(depotId))
                {
                    result.Manifests[depotId] = new ManifestEntry
                        { DepotId = depotId, ManifestId = manifestId, Data = data };
                }
            }

            // Also try to get Key.vdf from main/master branch for depot keys
            await TryFetchKeyVdfAsync(repo, appId, result, headers, tokenIdx);

            return result.Manifests.Count > 0 ? result : null;
        }
        catch (Exception ex) { Logger.Debug($"[{repo}] tags: {ex.Message}"); return null; }
    }

    private async Task TryFetchKeyVdfAsync(
        string repo, uint appId, ManifestResult result,
        Dictionary<string, string> headers, int tokenIdx)
    {
        foreach (var branch in new[] { "main", "master" })
        {
            try
            {
                var url = $"https://api.github.com/repos/{repo}/contents/{appId}/Key.vdf";
                var (h, _) = BuildGitHubHeaders();
                var json = await FetchJsonAsync(url, h, tokenIdx);
                if (json?["download_url"] == null) continue;
                string dlUrl = json["download_url"]!.ToString();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var resp = await _http.GetAsync(dlUrl, cts.Token);
                if (!resp.IsSuccessStatusCode) continue;
                var vdfBytes = await resp.Content.ReadAsByteArrayAsync();
                ParseKeyVdf(vdfBytes, result, null);
                return;
            }
            catch { }
        }
    }

    // ─── GitHub branch source ─────────────────────────────────────────────

    private async Task<ManifestResult?> TryGitHubBranchAsync(
        uint appId, string repo, byte[]? xorDecryptKey)
    {
        try
        {
            Logger.Debug($"[{repo}] checking branch {appId}...");
            var (headers, tokenIdx) = BuildGitHubHeaders();

            var branchUrl  = $"https://api.github.com/repos/{repo}/branches/{appId}";
            var branchJson = await FetchJsonAsync(branchUrl, headers, tokenIdx);
            if (branchJson == null || !branchJson.ContainsKey("commit")) return null;

            string sha     = branchJson["commit"]!["sha"]!.ToString();
            string treeUrl = branchJson["commit"]!["commit"]!["tree"]!["url"]!.ToString();

            var treeJson = await FetchJsonAsync(treeUrl, headers, tokenIdx);
            if (treeJson == null || !treeJson.ContainsKey("tree")) return null;

            var tree   = treeJson["tree"]!.ToArray();
            var result = new ManifestResult { AppId = appId, Source = repo };

            // Download all .manifest binaries in parallel within this repo
            var manifestDownloads = tree
                .Where(i => i["path"]!.ToString().EndsWith(".manifest"))
                .Select(async item =>
                {
                    string path  = item["path"]!.ToString();
                    var parts    = Path.GetFileNameWithoutExtension(path).Split('_');
                    if (parts.Length < 2)                                 return;
                    if (!uint.TryParse(parts[0],  out uint  depotId))    return;
                    if (!ulong.TryParse(parts[1], out ulong manifestId)) return;

                    byte[]? data = await FetchRawAsync(sha, path, repo);
                    if (data == null) return;

                    lock (result)
                    {
                        result.Manifests[depotId] = new ManifestEntry
                        {
                            DepotId    = depotId,
                            ManifestId = manifestId,
                            Data       = data
                        };
                    }
                    Logger.Info($"[{repo}] manifest {depotId}_{manifestId} ✓");
                });

            await Task.WhenAll(manifestDownloads);

            // Depot keys from Key.vdf (or config.vdf for some repos)
            foreach (var vdfName in new[] { "key.vdf", "Key.vdf", "config.vdf" })
            {
                var keyEntry = tree.FirstOrDefault(i =>
                    string.Equals(i["path"]!.ToString(), vdfName,
                        StringComparison.OrdinalIgnoreCase));
                if (keyEntry == null) continue;

                byte[]? keyData = await FetchRawAsync(sha, keyEntry["path"]!.ToString(), repo);
                if (keyData != null)
                {
                    ParseKeyVdf(keyData, result, xorDecryptKey);
                    break; // found one, stop
                }
            }

            return result.Manifests.Count > 0 || result.DepotKeys.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[{repo}] {ex.Message}");
            return null;
        }
    }

    // ─── luckygametools/steam-cfg ─────────────────────────────────────────

    private async Task<ManifestResult?> TryLuckyGameToolsAsync(uint appId)
    {
        const string repo = "luckygametools/steam-cfg";
        try
        {
            Logger.Debug($"[{repo}] trying...");
            var (headers, tokenIdx) = BuildGitHubHeaders();

            var contentsUrl  = $"https://api.github.com/repos/{repo}/contents/steamdb2/{appId}";
            var contentsJson = await FetchJsonArrayAsync(contentsUrl, headers, tokenIdx);
            if (contentsJson == null) return null;

            string? datPath = contentsJson
                .FirstOrDefault(i => i["name"]?.ToString() == "00000encrypt.dat")
                ?["path"]?.ToString();
            if (datPath == null) return null;

            byte[]? raw = await FetchRawAsync("main", datPath, repo);
            if (raw == null) return null;

            byte[]? aesDecrypted = SymmetricDecrypt(LuckyAesKey, raw);
            if (aesDecrypted == null) return null;
            byte[] xorDecrypted = XorDecrypt(LuckyXorKey, aesDecrypted);

            var result = await ParseGobViaPythonAsync(appId, xorDecrypted)
                      ?? ParseGobViaVzScanner(appId, xorDecrypted);

            if (result != null && (result.Manifests.Count > 0 || result.DepotKeys.Count > 0))
            {
                result.Source = repo;
                return result;
            }
        }
        catch (Exception ex) { Logger.Debug($"[{repo}] {ex.Message}"); }
        return null;
    }

    private static async Task<ManifestResult?> ParseGobViaPythonAsync(
        uint appId, byte[] gobData)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Scripts", "parse_luckygob.py"),
            Path.Combine(AppContext.BaseDirectory, "parse_luckygob.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "Scripts", "parse_luckygob.py"),
        };
        string? script = candidates.FirstOrDefault(File.Exists);
        if (script == null)
        {
            Logger.Debug("parse_luckygob.py not found — falling back to VZ scanner");
            return null;
        }

        try
        {
            string tmpIn  = Path.GetTempFileName();
            string tmpOut = Path.GetTempFileName();
            await File.WriteAllBytesAsync(tmpIn, gobData);

            foreach (string pythonExe in new[] { "python", "python3" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName               = pythonExe,
                        Arguments              = $"\"{script}\" \"{tmpIn}\" \"{tmpOut}\"",
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };
                    using var proc = Process.Start(psi)!;
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode == 0 && File.Exists(tmpOut))
                    {
                        string json = await File.ReadAllTextAsync(tmpOut);
                        File.Delete(tmpIn); File.Delete(tmpOut);
                        return ParseGobJson(appId, json);
                    }
                    string err = await proc.StandardError.ReadToEndAsync();
                    Logger.Debug($"parse_luckygob.py [{pythonExe}] exit {proc.ExitCode}: {err.Trim()}");
                }
                catch { }
            }
            File.Delete(tmpIn); File.Delete(tmpOut);
        }
        catch (Exception ex) { Logger.Debug($"ParseGobViaPythonAsync: {ex.Message}"); }
        return null;
    }

    private static ManifestResult? ParseGobJson(uint appId, string json)
    {
        try
        {
            var doc    = JsonDocument.Parse(json);
            var result = new ManifestResult { AppId = appId };

            foreach (var depot in doc.RootElement.GetProperty("depots").EnumerateArray())
            {
                uint  depotId    = depot.GetProperty("id").GetUInt32();
                ulong manifestId = depot.GetProperty("manifestId").GetUInt64();

                if (depot.TryGetProperty("decryptKey", out var keyElem))
                {
                    string hex = keyElem.GetString() ?? "";
                    if (hex.Length == 64)
                        result.DepotKeys[depotId] = Convert.FromHexString(hex);
                }

                byte[]? data = null;
                if (depot.TryGetProperty("manifestData", out var dataElem))
                {
                    string b64 = dataElem.GetString() ?? "";
                    if (!string.IsNullOrEmpty(b64)) data = Convert.FromBase64String(b64);
                }

                result.Manifests[depotId] = new ManifestEntry
                    { DepotId = depotId, ManifestId = manifestId, Data = data };
            }
            return result;
        }
        catch (Exception ex) { Logger.Debug($"ParseGobJson: {ex.Message}"); return null; }
    }

    private static ManifestResult? ParseGobViaVzScanner(uint appId, byte[] data)
    {
        Logger.Debug($"[luckygametools] VZ scanner: scanning {data.Length} bytes...");
        int found = 0;

        for (int i = 0; i < data.Length - 4; i++)
        {
            if (data[i] != 0x56 || data[i + 1] != 0x5A) continue;
            int[] trySizes = { 2 * 1024 * 1024, 1024 * 1024, 512 * 1024, 256 * 1024 };
            foreach (int sz in trySizes)
            {
                int end = Math.Min(i + sz, data.Length);
                try
                {
                    var dm = SteamKit2.DepotManifest.Deserialize(data[i..end]);
                    if (dm?.Files == null || dm.Files.Count == 0) continue;
                    found++;
                    Logger.Debug($"[luckygametools] VZ hit @{i}: {dm.Files.Count} files");
                    break;
                }
                catch { }
            }
        }

        if (found == 0) Logger.Debug("[luckygametools] VZ scanner: no valid manifests found");
        return null; // depot IDs unavailable without full gob parsing
    }

    // ─── printedwaste.com ────────────────────────────────────────────────

    private async Task<ManifestResult?> TryPrintedWasteAsync(uint appId)
    {
        const string source = "printedwaste.com";
        try
        {
            Logger.Debug($"[{source}] trying...");
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.printedwaste.com/gfk/download/{appId}");
            req.Headers.TryAddWithoutValidation("Authorization",
                "Bearer dGhpc19pcyBhX3JhbmRvbV90b2tlbg==");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await ParseZipSourceAsync(appId, source,
                await resp.Content.ReadAsByteArrayAsync());
        }
        catch (Exception ex) { Logger.Debug($"[{source}] {ex.Message}"); return null; }
    }

    // ─── steambox.gdata.fun ──────────────────────────────────────────────

    private async Task<ManifestResult?> TryGdataAsync(uint appId)
    {
        const string source = "steambox.gdata.fun";
        try
        {
            Logger.Debug($"[{source}] trying...");
            var resp = await _http.GetAsync(
                $"https://steambox.gdata.fun/cnhz/qingdan/{appId}.zip");
            if (!resp.IsSuccessStatusCode) return null;
            return await ParseZipSourceAsync(appId, source,
                await resp.Content.ReadAsByteArrayAsync());
        }
        catch (Exception ex) { Logger.Debug($"[{source}] {ex.Message}"); return null; }
    }

    // ─── cysaw.top ───────────────────────────────────────────────────────

    private async Task<ManifestResult?> TryCysawAsync(uint appId)
    {
        const string source = "cysaw.top";
        try
        {
            Logger.Debug($"[{source}] trying...");
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://cysaw.top/uploads/{appId}.zip");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await ParseZipSourceAsync(appId, source,
                await resp.Content.ReadAsByteArrayAsync());
        }
        catch (Exception ex) { Logger.Debug($"[{source}] {ex.Message}"); return null; }
    }

    // ─── manifesthub1.filegear-sg.me REST API ───────────────────────────────
    // The official backend API for ManifestHub. Given a depotId + manifestId
    // (obtained from the GitHub branch data), returns the manifest binary.
    // Requires MANIFESTHUB_API_KEY in .env — get one free at manifesthub1.filegear-sg.me
    // This is the highest-coverage source: all ManifestHub repos feed into it.

    private async Task<ManifestResult?> TryManifestHubApiAsync(uint appId)
    {
        const string source = "manifesthub1.filegear-sg.me";
        string apiKey = EmbeddedConfig.ManifestHubApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Logger.Debug($"[{source}] skipped — no MANIFESTHUB_API_KEY in .env");
            return null;
        }

        try
        {
            // Step 1: find all depotId+manifestId combos for this app from GitHub sources
            // We do a lightweight branch check on the SteamAutoCracks repo to get IDs
            var (headers, tokenIdx) = BuildGitHubHeaders();
            var branchUrl  = $"https://api.github.com/repos/SteamAutoCracks/ManifestHub/branches/{appId}";
            var branchJson = await FetchJsonAsync(branchUrl, headers, tokenIdx);
            if (branchJson == null) return null;

            string sha     = branchJson["commit"]!["sha"]!.ToString();
            string treeUrl = branchJson["commit"]!["commit"]!["tree"]!["url"]!.ToString();
            var treeJson   = await FetchJsonAsync(treeUrl, headers, tokenIdx);
            if (treeJson == null) return null;

            var result = new ManifestResult { AppId = appId, Source = source };

            // Step 2: for each .manifest file listed in the tree, fetch via API
            foreach (var node in treeJson["tree"]!)
            {
                string? name = node["path"]?.ToString();
                if (name == null || !name.EndsWith(".manifest")) continue;

                var parts = Path.GetFileNameWithoutExtension(name).Split('_');
                if (parts.Length < 2) continue;
                if (!uint.TryParse(parts[0], out uint depotId)) continue;
                if (!ulong.TryParse(parts[1], out ulong manifestId)) continue;

                // Fetch manifest binary from the API
                var url = $"https://api.manifesthub1.filegear-sg.me/manifest" +
                          $"?apikey={Uri.EscapeDataString(apiKey)}" +
                          $"&depotid={depotId}&manifestid={manifestId}";
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var resp = await _http.GetAsync(url, cts.Token);
                    if (!resp.IsSuccessStatusCode) continue;

                    byte[] data = await resp.Content.ReadAsByteArrayAsync();
                    if (data.Length < 10) continue;

                    result.Manifests[depotId] = new ManifestEntry
                        { DepotId = depotId, ManifestId = manifestId, Data = data };
                    Logger.Debug($"[{source}] depot {depotId} manifest {manifestId} ✓");
                }
                catch { }
            }

            return result.Manifests.Count > 0 ? result : null;
        }
        catch (Exception ex) { Logger.Debug($"[{source}] {ex.Message}"); return null; }
    }

    // ─── depotbox.org ────────────────────────────────────────────────────────
    // Tracks 60,000+ depots, updated frequently.
    // API key required — register free at depotbox.org, then request an API key from the dashboard.
    // Must be in .env as DEPOTBOX_API_KEY.

    private async Task<ManifestResult?> TryDepotBoxAsync(uint appId)
    {
        const string source = "depotbox.org";
        string apiKey = EmbeddedConfig.DepotBoxApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Logger.Debug($"[{source}] skipped — no DEPOTBOX_API_KEY in .env");
            return null;
        }

        try
        {
            Logger.Debug($"[{source}] trying app {appId}...");

            // DepotBox API: GET /api/download?appid=<id>&apikey=<key>
            // Returns a zip with manifests and keys, same format as other ZIP sources
            var url = $"https://depotbox.org/api/download?appid={appId}" +
                      $"&apikey={Uri.EscapeDataString(apiKey)}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] zipBytes = await resp.Content.ReadAsByteArrayAsync();
            if (zipBytes.Length < 10) return null;

            // Verify it's actually a ZIP (PK magic)
            if (zipBytes[0] != 0x50 || zipBytes[1] != 0x4B) return null;

            return await ParseZipSourceAsync(appId, source, zipBytes);
        }
        catch (Exception ex) { Logger.Debug($"[{source}] {ex.Message}"); return null; }
    }

    // ─── manifest.youngzm.com ────────────────────────────────────────────────
    // Web-based manifest downloader — no key required, free to use.
    // Downloads from the same GitHub sources but via a CDN proxy.

    private async Task<ManifestResult?> TryManifestYoungzmAsync(uint appId)
    {
        const string source = "manifest.youngzm.com";
        try
        {
            Logger.Debug($"[{source}] trying app {appId}...");

            // Endpoint fetches and zips all manifests for the appId
            var url = $"https://manifest.youngzm.com/api/download?appid={appId}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] data = await resp.Content.ReadAsByteArrayAsync();
            if (data.Length < 10) return null;
            if (data[0] != 0x50 || data[1] != 0x4B) return null;

            return await ParseZipSourceAsync(appId, source, data);
        }
        catch (Exception ex) { Logger.Debug($"[{source}] {ex.Message}"); return null; }
    }

    // ─── Zip / .st / .lua / Key.vdf parser ──────────────────────────────

    private async Task<ManifestResult?> ParseZipSourceAsync(
        uint appId, string source, byte[] zipBytes)
    {
        var result = new ManifestResult { AppId = appId, Source = source };
        using var ms  = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            string name = Path.GetFileName(entry.FullName);

            if (name.EndsWith(".manifest"))
            {
                var parts = Path.GetFileNameWithoutExtension(name).Split('_');
                if (parts.Length >= 2 &&
                    uint.TryParse(parts[0],  out uint  dId) &&
                    ulong.TryParse(parts[1], out ulong mId))
                {
                    using var s = entry.Open(); using var buf = new MemoryStream();
                    await s.CopyToAsync(buf);
                    result.Manifests[dId] = new ManifestEntry
                        { DepotId = dId, ManifestId = mId, Data = buf.ToArray() };
                    Logger.Debug($"[{source}] manifest {name} ✓");
                }
            }
            else if (name.EndsWith(".st"))
            {
                using var s = entry.Open(); using var buf = new MemoryStream();
                await s.CopyToAsync(buf);
                ParseStFile(appId, buf.ToArray(), result);
            }
            else if (name.EndsWith(".lua"))
            {
                using var s  = entry.Open();
                using var sr = new StreamReader(s, Encoding.UTF8);
                ParseLuaContent(appId, await sr.ReadToEndAsync(), result);
            }
            else if (name.ToLowerInvariant() is "key.vdf" or "config.vdf")
            {
                using var s = entry.Open(); using var buf = new MemoryStream();
                await s.CopyToAsync(buf);
                ParseKeyVdf(buf.ToArray(), result, null);
            }
        }

        return result.Manifests.Count > 0 || result.DepotKeys.Count > 0 ? result : null;
    }

    // ─── .st parser ──────────────────────────────────────────────────────

    private void ParseStFile(uint appId, byte[] data, ManifestResult result)
    {
        try
        {
            if (data.Length < 12) return;
            uint xorKey = BitConverter.ToUInt32(data, 0);
            uint size   = BitConverter.ToUInt32(data, 4);
            xorKey ^= 0xFFFEA4C8; xorKey &= 0xFF;
            byte[] body = new byte[size];
            Array.Copy(data, 12, body, 0, (int)Math.Min(size, data.Length - 12));
            for (int i = 0; i < body.Length; i++) body[i] ^= (byte)xorKey;
            byte[] dec = Decompress(body);
            string lua = Encoding.UTF8.GetString(dec, 512, Math.Max(0, dec.Length - 512));
            ParseLuaContent(appId, lua, result);
        }
        catch (Exception ex) { Logger.Debug($"ParseStFile: {ex.Message}"); }
    }

    // ─── Lua parser ──────────────────────────────────────────────────────

    private static readonly Regex AddAppIdRx = new(
        @"addappid\(\s*(\d+)\s*(?:,\s*\d+\s*,\s*""([0-9a-fA-F]+)""\s*)?\)",
        RegexOptions.Compiled);

    private static readonly Regex SetManifestRx = new(
        @"setManifestid\(\s*(\d+)\s*,\s*""(\d+)""\s*(?:,\s*\d+\s*)?\)",
        RegexOptions.Compiled);

    private static void ParseLuaContent(uint appId, string content, ManifestResult result)
    {
        foreach (Match m in AddAppIdRx.Matches(content))
        {
            if (!uint.TryParse(m.Groups[1].Value, out uint depotId)) continue;
            if (m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value))
            {
                result.DepotKeys[depotId] = Convert.FromHexString(m.Groups[2].Value);
                Logger.Debug($"Lua key: depot {depotId}");
            }
        }
        foreach (Match m in SetManifestRx.Matches(content))
        {
            if (!uint.TryParse(m.Groups[1].Value,  out uint  depotId))    continue;
            if (!ulong.TryParse(m.Groups[2].Value, out ulong manifestId)) continue;
            if (!result.Manifests.ContainsKey(depotId))
                result.Manifests[depotId] = new ManifestEntry
                    { DepotId = depotId, ManifestId = manifestId, Data = null };
            Logger.Debug($"Lua manifest: depot {depotId} → {manifestId}");
        }
    }

    // ─── Key.vdf / config.vdf parser ─────────────────────────────────────


    // ═══════════════════════════════════════════════════════════════════════════
    // STEAMTOOLS LUA ECOSYSTEM
    // ═══════════════════════════════════════════════════════════════════════════
    //
    // SteamTools is a Chinese Steam client emulator. It uses .lua files that
    // contain depot decryption keys in a different format from Key.vdf.
    //
    // Lua file format:
    //   addappid(appId, 1, "")                 -- app header (empty key)
    //   addappid(depotId, 2, "hexdepotkey")    -- per-depot key
    //   setManifestid(depotId, manifestId)     -- manifest ID (optional)
    //
    // These sources are completely independent from the ManifestAutoUpdate
    // GitHub ecosystem and often have different games covered.
    // ═══════════════════════════════════════════════════════════════════════════

    // Known Lua-serving endpoints (no auth needed)
    private static readonly (string Url, string Source)[] LuaApiEndpoints =
    {
        // ── openlua.cloud ─────────────────────────────────────────────────────
        // Previously known as Manifestor.cc — largest Lua database
        // Returns raw .lua or .zip with manifest + key.vdf
        ("https://api.openlua.cloud/lua/{0}",          "openlua.cloud"),
        ("https://openlua.cloud/api/get?appid={0}",    "openlua.cloud/api"),

        // ── steamml.vercel.app ────────────────────────────────────────────────
        // Free, ad-free, open source — returns JSON or zip
        ("https://steamml.vercel.app/api/manifest?appid={0}",  "steamml.vercel.app"),
        ("https://steamml.vercel.app/api/lua?appid={0}",        "steamml.vercel.app/lua"),

        // ── steamtools.pages.dev ──────────────────────────────────────────────
        // Multi-source download, SteamCMD-compatible structure
        ("https://steamtools.pages.dev/api/get?appid={0}",     "steamtools.pages.dev"),
        ("https://steamtools.pages.dev/api/manifest/{0}",      "steamtools.pages.dev/manifest"),

        // ── walftech.com ──────────────────────────────────────────────────────
        // "Number one platform" — has manifest + lua + DLC unlocker
        // Returns ManifestHub-{appId}.zip with standard file structure
        ("https://walftech.com/api/generate?appid={0}",        "walftech.com"),
        ("https://walftech.com/api/manifest?appid={0}",        "walftech.com/manifest"),

        // ── steammanifest.com ─────────────────────────────────────────────────
        // Walftech division — separate database, different coverage
        ("https://steammanifest.com/api/generate?appid={0}",   "steammanifest.com"),
        ("https://steammanifest.com/api/manifest?appid={0}",   "steammanifest.com/manifest"),

        // ── steamtools.site ───────────────────────────────────────────────────
        // Standalone manifest + lua generator
        ("https://steamtools.site/api?appid={0}",              "steamtools.site"),
        ("https://steamtools.site/generate?appid={0}",         "steamtools.site/generate"),

        // ── manifestlua.blog ─────────────────────────────────────────────────
        // Returns ManifestHub-{appId}.zip with manifest + lua + key.vdf
        ("https://manifestlua.blog/api/generate?appid={0}",    "manifestlua.blog"),
        ("https://manifestlua.blog/download?appid={0}",        "manifestlua.blog/download"),

        // ── ssmg4 ManifestHubDownloader ───────────────────────────────────────
        // Frontend for ManifestHub, returns zip
        ("https://ssmg4.github.io/ManifestHubDownloader/api?appid={0}", "ssmg4.github.io"),

        // ── manifest.youngzm.com ──────────────────────────────────────────────
        // GitHub CDN proxy, no key needed
        ("https://manifest.youngzm.com/api/download?appid={0}", "youngzm.com"),
        ("https://manifest.youngzm.com/download?id={0}",        "youngzm.com/v2"),
    };

    private async Task<ManifestResult?> TryLuaSourcesAsync(uint appId)
    {
        var tasks = LuaApiEndpoints
            .Select(e => TryLuaEndpointAsync(appId, e.Url, e.Source))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var merged  = new ManifestResult { AppId = appId, Source = "lua-sources" };
        foreach (var r in results.Where(r => r != null))
            MergeInto(merged, r!);

        return merged.DepotKeys.Count > 0 || merged.Manifests.Count > 0 ? merged : null;
    }

    private async Task<ManifestResult?> TryLuaEndpointAsync(
        uint appId, string urlTemplate, string source)
    {
        try
        {
            Logger.Debug($"[{source}] trying lua for app {appId}...");
            string url = string.Format(urlTemplate, appId);
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            string ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            byte[] data = await resp.Content.ReadAsByteArrayAsync();
            if (data.Length < 5) return null;

            var result = new ManifestResult { AppId = appId, Source = source };

            // ZIP response — may contain .lua + .manifest files
            if (ct.Contains("zip") || (data[0] == 0x50 && data[1] == 0x4B))
            {
                var zipResult = await ParseZipSourceAsync(appId, source, data);
                if (zipResult != null)
                {
                    // Also try to parse .lua files within the zip for more keys
                    await TryParseLuaFilesFromZipAsync(appId, source, data, zipResult);
                    return zipResult;
                }
            }

            // JSON response — some APIs return structured data
            if (ct.Contains("json"))
            {
                try
                {
                    var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(data));
                    ParseLuaJson(json, appId, result);
                    return result.DepotKeys.Count > 0 ? result : null;
                }
                catch { }
            }

            // Raw .lua file
            string luaText = System.Text.Encoding.UTF8.GetString(data);
            if (luaText.Contains("addappid") || luaText.Contains("setManifestid"))
            {
                ParseLuaText(luaText, result);
                return result.DepotKeys.Count > 0 || result.Manifests.Count > 0 ? result : null;
            }

            return null;
        }
        catch (Exception ex) { Logger.Debug($"[{source}] lua: {ex.Message}"); return null; }
    }

    private async Task TryParseLuaFilesFromZipAsync(
        uint appId, string source, byte[] zipBytes, ManifestResult result)
    {
        try
        {
            using var ms  = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.Where(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith(".st",  StringComparison.OrdinalIgnoreCase)))
            {
                using var s   = entry.Open();
                using var buf = new MemoryStream();
                await s.CopyToAsync(buf);
                ParseLuaText(System.Text.Encoding.UTF8.GetString(buf.ToArray()), result);
            }
        }
        catch { }
    }

    /// <summary>
    /// Parse a SteamTools .lua (or .st) file and extract depot keys + manifest IDs.
    ///
    /// Common formats:
    ///   addappid(depotId, 2, "hexkey")
    ///   addappid(depotId, 2, "hexkey", manifestId)
    ///   setManifestid(depotId, manifestId)
    ///   addManifest(depotId, manifestId, "hexkey")
    /// </summary>
    private static void ParseLuaText(string lua, ManifestResult result)
    {
        try
        {
            // Delegate to the pre-compiled regex parser for core patterns
            ParseLuaContent(0, lua, result);

            // Extra: addManifest(depotId, manifestId, "hexkey") variant
            var addMfRx = new Regex(
                @"addManifest\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*""([0-9a-fA-F]*)""\s*\)",
                RegexOptions.Multiline);
            foreach (Match m in addMfRx.Matches(lua))
            {
                if (!uint.TryParse(m.Groups[1].Value,  out uint  dId)) continue;
                if (!ulong.TryParse(m.Groups[2].Value, out ulong mId)) continue;
                if (!result.Manifests.ContainsKey(dId))
                    result.Manifests[dId] = new ManifestEntry { DepotId = dId, ManifestId = mId };
                if (m.Groups[3].Value.Length == 64)
                    try { result.DepotKeys[dId] = Convert.FromHexString(m.Groups[3].Value); } catch { }
            }
        }
        catch (Exception ex) { Logger.Debug($"ParseLuaText: {ex.Message}"); }
    }

    /// <summary>Parse JSON response from Lua APIs that return structured data.</summary>
    private static void ParseLuaJson(JObject json, uint appId, ManifestResult result)
    {
        // steamml format: { "depots": { "731": { "manifestId": "...", "key": "hex" } } }
        var depots = json["depots"] as JObject ?? json["Depots"] as JObject;
        if (depots == null) return;

        foreach (var prop in depots.Properties())
        {
            if (!uint.TryParse(prop.Name, out uint depotId)) continue;
            var obj = prop.Value as JObject;
            if (obj == null) continue;

            string? key = obj["key"]?.ToString() ?? obj["Key"]?.ToString()
                       ?? obj["decryptionKey"]?.ToString();
            if (key?.Length == 64)
                try { result.DepotKeys[depotId] = Convert.FromHexString(key); } catch { }

            string? mfId = obj["manifestId"]?.ToString() ?? obj["ManifestId"]?.ToString()
                        ?? obj["manifest"]?.ToString();
            if (mfId != null && ulong.TryParse(mfId, out ulong mid))
                result.Manifests[depotId] = new ManifestEntry { DepotId = depotId, ManifestId = mid };
        }
    }

    private static void ParseKeyVdf(byte[] data, ManifestResult result, byte[]? xorKey)
    {
        try
        {
            string vdf = Encoding.UTF8.GetString(data);

            // Standard Key.vdf format: "depotId" { "DecryptionKey" "hexkey" }
            var depotRx = new Regex(
                @"""(\d+)""\s*\{[^}]*""DecryptionKey""\s*""([0-9a-fA-F]+)""",
                RegexOptions.Singleline);
            foreach (Match m in depotRx.Matches(vdf))
            {
                if (!uint.TryParse(m.Groups[1].Value, out uint depotId)) continue;
                byte[] key = Convert.FromHexString(m.Groups[2].Value);
                if (xorKey != null) key = XorDecrypt(xorKey, key);
                result.DepotKeys[depotId] = key;
                Logger.Debug($"Key.vdf: depot {depotId}");
            }

            // config.vdf format: "depots" { "depotId" { "DecryptionKey" "hexkey" } }
            // (same regex matches — the outer nesting doesn't matter for regex)
        }
        catch (Exception ex) { Logger.Debug($"ParseKeyVdf: {ex.Message}"); }
    }

    // ─── Crypto ──────────────────────────────────────────────────────────

    private static byte[]? SymmetricDecrypt(byte[] key, byte[] cipher)
    {
        try
        {
            using var ecb = Aes.Create();
            ecb.Key = key; ecb.Mode = CipherMode.ECB; ecb.Padding = PaddingMode.None;
            byte[] iv = new byte[16]; Array.Copy(cipher, iv, 16);
            iv = ecb.CreateDecryptor().TransformFinalBlock(iv, 0, 16);
            using var cbc = Aes.Create();
            cbc.Key = key; cbc.IV = iv; cbc.Mode = CipherMode.CBC; cbc.Padding = PaddingMode.PKCS7;
            return cbc.CreateDecryptor().TransformFinalBlock(cipher, 16, cipher.Length - 16);
        }
        catch (Exception ex) { Logger.Debug($"SymmetricDecrypt: {ex.Message}"); return null; }
    }

    private static byte[] XorDecrypt(byte[] key, byte[] data)
    {
        byte[] r = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ key[i % key.Length]);
        return r;
    }

    private static byte[] Decompress(byte[] data)
    {
        using var ms   = new MemoryStream(data);
        using var gz   = new DeflateStream(ms, CompressionMode.Decompress);
        using var out_ = new MemoryStream();
        gz.CopyTo(out_);
        return out_.ToArray();
    }

    // ─── HTTP ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Build headers using the next token in the round-robin pool.
    /// Each call rotates to the next token so load is spread evenly.
    /// </summary>
    private (Dictionary<string, string> headers, int tokenIdx) BuildGitHubHeaders()
    {
        var h = new Dictionary<string, string>();
        if (_githubTokens.Length == 0) return (h, 0);

        // Atomic round-robin: each call gets the next token index
        int idx = Interlocked.Increment(ref _tokenIndex) % _githubTokens.Length;
        h["Authorization"] = $"Bearer {_githubTokens[idx]}";
        return (h, idx);
    }

    /// <summary>Overload for callers that don't need the token index.</summary>
    private Dictionary<string, string> BuildGitHubHeadersSimple() =>
        BuildGitHubHeaders().headers;

    private async Task<JObject?> FetchJsonAsync(
        string url, Dictionary<string, string> headers, int tokenIdx = -1)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            var resp = await _http.SendAsync(req);

            if ((int)resp.StatusCode is 429)
            { MarkTokenRateLimited(tokenIdx, url); return null; }

            if ((int)resp.StatusCode == 403 && url.Contains("api.github.com"))
            {
                // Only treat as rate-limit if X-RateLimit-Remaining is 0
                // A regular 403 (private/deleted repo) still has remaining > 0
                bool isRateLimit = !resp.Headers.TryGetValues("X-RateLimit-Remaining",
                    out var vals) || vals.FirstOrDefault() == "0";
                if (isRateLimit) MarkTokenRateLimited(tokenIdx, url);
                return null;
            }

            if (!resp.IsSuccessStatusCode) return null;
            return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private async Task<JArray?> FetchJsonArrayAsync(
        string url, Dictionary<string, string> headers, int tokenIdx = -1)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            var resp = await _http.SendAsync(req);

            if ((int)resp.StatusCode is 429)
            { MarkTokenRateLimited(tokenIdx, url); return null; }

            if ((int)resp.StatusCode == 403 && url.Contains("api.github.com"))
            {
                bool isRateLimit = !resp.Headers.TryGetValues("X-RateLimit-Remaining",
                    out var vals) || vals.FirstOrDefault() == "0";
                if (isRateLimit) MarkTokenRateLimited(tokenIdx, url);
                return null;
            }

            if (!resp.IsSuccessStatusCode) return null;
            return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private void MarkTokenRateLimited(int tokenIdx, string url)
    {
        if (!url.Contains("api.github.com")) return;

        if (tokenIdx >= 0 && tokenIdx < _tokenRateLimited.Length)
        {
            _tokenRateLimited[tokenIdx] = true;
            Logger.Debug($"GitHub token #{tokenIdx + 1} rate-limited — switching to other token(s).");
        }

        bool allExhausted = _githubTokens.Length == 0 || _tokenRateLimited.All(f => f);
        if (allExhausted && Interlocked.CompareExchange(ref _anyTokenRateLimited, 1, 0) == 0)
        {
            string msg = _githubTokens.Length == 0
                ? "GitHub rate-limited (anonymous). Add GITHUB_API_KEY_PAT and GITHUB_API_KEY_CLASSIC to .env for 10k req/hr."
                : _githubTokens.Length == 1
                    ? "GitHub token rate-limited. Add GITHUB_API_KEY_CLASSIC to .env for a second token."
                    : "All GitHub tokens rate-limited. Limits reset at the top of each hour.";
            Logger.Warn(msg);
        }
    }

    private async Task<byte[]?> FetchRawAsync(string sha, string path, string repo)
    {
        // Multiple CDN mirrors — tries each in order until one responds 200
        string[] urls =
        {
            $"https://raw.githubusercontent.com/{repo}/{sha}/{path}",
            $"https://raw.gitmirror.com/{repo}/{sha}/{path}",
            $"https://ghfast.top/https://raw.githubusercontent.com/{repo}/{sha}/{path}",
            $"https://cdn.jsdmirror.com/gh/{repo}@{sha}/{path}",
            $"https://raw.dgithub.xyz/{repo}/{sha}/{path}",
            $"https://gh.akass.cn/{repo}/{sha}/{path}",
            $"https://jsdelivr.pai233.top/gh/{repo}@{sha}/{path}",
            $"https://github.moeyy.xyz/https://raw.githubusercontent.com/{repo}/{sha}/{path}",
        };
        foreach (var url in urls)
        {
            try
            {
                using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await _http.GetAsync(url, cts.Token);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsByteArrayAsync();
            }
            catch { }
        }
        return null;
    }
}

// ─── Result types ─────────────────────────────────────────────────────────────

public class ManifestResult
{
    public uint   AppId  { get; set; }
    public string Source { get; set; } = "";
    public Dictionary<uint, ManifestEntry> Manifests { get; } = new();
    public Dictionary<uint, byte[]>        DepotKeys { get; } = new();
}

public class ManifestEntry
{
    public uint    DepotId    { get; set; }
    public ulong   ManifestId { get; set; }
    public byte[]? Data       { get; set; }
    // Source tracking — used by DownloadSessionBuilder to fetch binary from CDN directly
    public string? Repo       { get; set; }
    public string? BranchSha  { get; set; }
}
