using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using LustsDepotDownloaderPro.Core;
using LustsDepotDownloaderPro.Steam;
using LustsDepotDownloaderPro.UI;
using LustsDepotDownloaderPro.Utils;
using LustsDepotDownloaderPro.Models;
using Spectre.Console;

namespace LustsDepotDownloaderPro;

class Program
{
    private static DownloadSession? _currentSession;
    private static CancellationTokenSource? _cts;
    private static DownloadEngine? _engine;

    // ─── Core download options ────────────────────────────────────────────────

    private static readonly Option<uint>    OptApp      = new(new[]{"--app","-a"},     ()=>0u,              "AppID to download");
    private static readonly Option<uint?>   OptDepot    = new(new[]{"--depot","-d"},                        "DepotID (all if omitted)");
    private static readonly Option<ulong?>  OptManifest = new(new[]{"--manifest","-m"},                     "Manifest ID");
    private static readonly Option<string>  OptBranch   = new(new[]{"--branch","-b"},  ()=>"public",        "Branch");
    private static readonly Option<string?> OptBranchPassword = new(new[]{"--branch-password","-bp"},       "Branch password");
    private static readonly Option<string?> OptUsername = new(new[]{"--username","-u"},                     "Steam username");
    private static readonly Option<string?> OptPassword = new(new[]{"--password","-p"},                     "Steam password");
    private static readonly Option<bool>    OptRememberPass   = new(new[]{"--remember-password","-rp"}, ()=>false, "Save credentials");
    private static readonly Option<bool>    OptQr             = new(new[]{"--qr"},              ()=>false,   "QR code login");
    private static readonly Option<string?> OptSharedSecret   = new(new[]{"--shared-secret","-ss"},
        "Base64 shared_secret from Steam Guard maFile — enables automatic 2FA code generation");
    private static readonly Option<string?> OptDepotKeys      = new(new[]{"--depot-keys","-dk"},             "Depot keys file");
    private static readonly Option<string?> OptManifestFile   = new(new[]{"--manifest-file","-mf"},          "Local manifest file");
    private static readonly Option<string?> OptAppToken       = new(new[]{"--app-token","-at"},              "App access token");
    private static readonly Option<string?> OptPackageToken   = new(new[]{"--package-token","-pt"},          "Package access token");
    private static readonly Option<string>  OptOutput         = new(new[]{"--output","-o"}, ()=>Environment.CurrentDirectory, "Output directory");
    private static readonly Option<string?> OptFileList       = new(new[]{"--filelist","-fl"},               "File filter list");
    private static readonly Option<bool>    OptValidate       = new(new[]{"--validate","-v"}, ()=>false,     "Verify checksums after download");
    private static readonly Option<int>     OptMaxDownloads   = new(new[]{"--max-downloads","-md"}, ()=>8,   "Parallel workers (1–64)");
    private static readonly Option<int>     OptFallbackWorkers = new(new[]{"--fallback-workers","-fw"}, ()=>-1,  "Fallback (direct-HTTP) workers. Default: auto (25% of total). 0=primary only.");
    private static readonly Option<int?>    OptCellId         = new(new[]{"--cellid","-c"},                  "CDN cell ID override");
    private static readonly Option<uint?>   OptLoginId        = new(new[]{"--loginid","-lid"},               "Steam login ID");
    private static readonly Option<string>  OptOs             = new(new[]{"--os"},             GetCurrentOS, "Target OS (windows/macos/linux)");
    private static readonly Option<string>  OptOsArch         = new(new[]{"--os-arch","-arch"},GetCurrentArch,"Architecture (32/64)");
    private static readonly Option<string>  OptLanguage       = new(new[]{"--language","-lang"},()=>"english","Language");
    private static readonly Option<bool>    OptAllPlatforms   = new(new[]{"--all-platforms","-ap"},()=>false, "Include all platform depots");
    private static readonly Option<bool>    OptAllArchs       = new(new[]{"--all-archs","-aa"}, ()=>false,   "Include all architecture depots");
    private static readonly Option<bool>    OptAllLanguages   = new(new[]{"--all-languages","-al"},()=>false,"Include all language depots");
    private static readonly Option<bool>    OptLowViolence    = new(new[]{"--low-violence","-lv"},()=>false, "Low-violence depots");
    private static readonly Option<ulong?>  OptPubFile        = new(new[]{"--pubfile","-pf"},                "Workshop PublishedFileId");
    private static readonly Option<ulong?>  OptUgc            = new(new[]{"--ugc"},                          "Workshop UGC ID");
    private static readonly Option<bool>    OptManifestOnly   = new(new[]{"--manifest-only","-mo"},()=>false,"Print manifest IDs only");
    private static readonly Option<bool>    OptDebug          = new(new[]{"--debug"},          ()=>false,   "Verbose debug logging");
    private static readonly Option<string?> OptApiKey         = new(new[]{"--api-key","-key"},              "GitHub API key (raises rate limit)");
    private static readonly Option<bool>    OptPause          = new(new[]{"--pause"},          ()=>false,   "Pause active download");
    private static readonly Option<bool>    OptResume         = new(new[]{"--resume","-r"},    ()=>false,   "Resume last download for --app (uses saved progress)");
    private static readonly Option<bool>    OptStatus         = new(new[]{"--status","-s"},    ()=>false,   "Show current download status");
    private static readonly Option<bool>    OptTerminalUi     = new(new[]{"--terminal-ui","-tui"},()=>true, "Live progress UI");

    // ─── Integrity / update options ───────────────────────────────────────────

    /// <summary>Verify game files in the output directory against the manifest.</summary>
    private static readonly Option<bool>    OptVerify_  = new(
        new[]{"--verify-files","--verify-integrity","-vi"},
        ()=>false,
        "Verify existing game files against manifest (like Steam's Verify Integrity)");

    /// <summary>Update installed game — re-download changed/missing files only.</summary>
    private static readonly Option<bool>    OptUpdate_  = new(
        new[]{"--update","-up"},
        ()=>false,
        "Update installed game: check build, verify files, re-download what changed");

    // ─── Info / management options ────────────────────────────────────────────

    /// <summary>Show all games stored in the local database (downloads + manifests).</summary>
    private static readonly Option<bool>    OptGames = new(
        new[]{"--games","--db"},
        ()=>false,
        "List all recorded games in the local database");

    /// <summary>Check one or all recorded games for available updates.</summary>
    private static readonly Option<bool>    OptUpdates = new(
        new[]{"--updates"},
        ()=>false,
        "Check for updates. Use with --app for one game, or alone for all");

    /// <summary>Save a preferred CDN region so every future download uses it.</summary>
    private static readonly Option<int?>    OptCdnRegion = new(
        new[]{"--cdn-region"},
        "Set preferred CDN region ID (saved; applied to all future downloads)");

    // ─── State ────────────────────────────────────────────────────────────────

    private static int _cancelHandled = 0;

    // ─── Entry ────────────────────────────────────────────────────────────────

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = $"Lusts Depot Downloader Pro v{EmbeddedConfig.AppVersion}";

        var root = new RootCommand("Lusts Depot Downloader Pro — Steam Depot Downloader")
        {
            // Download
            OptApp, OptDepot, OptManifest, OptBranch, OptBranchPassword,
            OptUsername, OptPassword, OptRememberPass, OptQr,
            OptDepotKeys, OptManifestFile, OptAppToken, OptPackageToken,
            OptOutput, OptFileList, OptValidate, OptMaxDownloads,
            OptFallbackWorkers, OptCellId, OptLoginId, OptOs, OptOsArch, OptLanguage,
            OptAllPlatforms, OptAllArchs, OptAllLanguages, OptLowViolence,
            OptPubFile, OptUgc, OptManifestOnly, OptDebug, OptApiKey, OptSharedSecret,
            OptVerify_, OptUpdate_,
            OptPause, OptResume, OptStatus, OptTerminalUi,
            // Info / management
            OptGames, OptUpdates, OptCdnRegion,
        };

        root.SetHandler(async ctx => await ExecuteAsync(ctx));
        Console.CancelKeyPress += OnCancelKeyPress;

        try { return await root.InvokeAsync(args); }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Interlocked.CompareExchange(ref _cancelHandled, 1, 0) == 0)
        {
            AnsiConsole.MarkupLine("\n[yellow]⏸ Pausing — saving progress...[/]");
            try { _cts?.Cancel(); } catch { }
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Force quitting...[/]");
            Environment.Exit(2);
        }
    }

    // ─── Dispatch ─────────────────────────────────────────────────────────────

    private static async Task<int> ExecuteAsync(InvocationContext ctx)
    {
        var r     = ctx.ParseResult;
        bool debug = r.GetValueForOption(OptDebug);

        Logger.Initialize(debug);
        Logger.QuietMode = !debug;

        // ── --games ───────────────────────────────────────────────────────────
        if (r.GetValueForOption(OptGames))
            return ShowGames();

        // ── --cdn-region <id> ─────────────────────────────────────────────────
        if (r.GetValueForOption(OptCdnRegion) is int regionId)
        {
            LocalDatabase.Instance.SetPreferredCellId(regionId);
            AnsiConsole.MarkupLine($"[green]✓ CDN region set to {regionId}[/]");
            return 0;
        }

        // ── --updates [--app <id>] ─────────────────────────────────────────────
        if (r.GetValueForOption(OptUpdates))
        {
            uint updateApp = r.GetValueForOption(OptApp);
            string? key    = EmbeddedConfig.ResolveApiKey(r.GetValueForOption(OptApiKey));
            return updateApp > 0
                ? await RunUpdateCheck(updateApp, key)
                : await RunUpdateCheckAll(key);
        }

        // ── --pause / --status ─────────────────────────────────────────────────
        if (r.GetValueForOption(OptPause))  { HandlePause(); return 0; }
        if (r.GetValueForOption(OptStatus)) { ShowStatus();  return 0; }

        // ── Download / Verify / Update ────────────────────────────────────────
        var opts = BuildOptions(r);

        // Apply saved region if no explicit cellid given
        if (!opts.CellId.HasValue)
            opts.CellId = LocalDatabase.Instance.GetPreferredCellId();

        var ui = opts.TerminalUi ? new TerminalUI() : null;
        ui?.ShowHeader();

        try
        {
            _cts = new CancellationTokenSource();
            var steam = new SteamSession(opts.CellId, opts.LoginId);

            ui?.ShowStatus("Authenticating with Steam...");
            if (!await AuthAsync(steam, opts, ui))
            {
                AnsiConsole.MarkupLine("[red]❌ Authentication failed[/]");
                return 1;
            }
            ui?.ShowStatus("✓ Connected to Steam");

            ui?.ShowStatus("Preparing session...");
            _currentSession = await BuildSessionAsync(steam, opts);
            if (_currentSession == null)
            {
                AnsiConsole.MarkupLine("[red]❌ Could not prepare download session[/]");
                return 1;
            }

            long totalFiles = _currentSession.Depots.Sum(d => (long)d.Files.Count);
            long totalBytes = _currentSession.Depots.Sum(d => d.Files.Sum(f => (long)f.Size));

            // ── Build info ─────────────────────────────────────────────────
            var  localRecord = LocalDatabase.Instance.GetGameRecord(_currentSession.AppId);
            uint localBuild  = localRecord?.BuildId ??
                IntegrityVerifier.GetLocalBuildId(_currentSession.AppId, _currentSession.OutputDir);
            uint steamBuild  = _currentSession.BuildId;

            // ── Verify / Update mode ────────────────────────────────────────
            if (opts.VerifyIntegrity || opts.UpdateMode)
            {
                ShowBuildInfo(_currentSession, localBuild, steamBuild);
                ui?.ShowStatus("Verifying game files...");
                VerifyResult? verifyResult = null;

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn { Width = 30 },
                             new PercentageColumn(), new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var vTask = ctx.AddTask(
                            $"[cyan]Verifying {EscapeMarkup(_currentSession.AppName)}[/]",
                            maxValue: totalFiles);
                        var prog = new Progress<VerifyProgress>(p =>
                        {
                            vTask.Value    = p.Done;
                            vTask.MaxValue = p.Total;
                        });
                        verifyResult = await IntegrityVerifier.VerifyAsync(
                            _currentSession.OutputDir, _currentSession.Depots, prog, _cts.Token);
                    });

                IntegrityVerifier.PrintVerifyResult(verifyResult!);

                // Persist verify result
                if (localRecord != null)
                {
                    localRecord.LastVerifiedAt = verifyResult!.VerifiedAt;
                    localRecord.VerifyStatus   = verifyResult.IsClean ? "ok" : "corrupt";
                }

                if (opts.VerifyIntegrity && !opts.UpdateMode)
                    return verifyResult!.IsClean ? 0 : 3;

                if (opts.UpdateMode)
                {
                    bool buildMismatch = steamBuild > 0 && localBuild != steamBuild;
                    bool hasIssues     = !verifyResult!.IsClean;
                    if (!buildMismatch && !hasIssues)
                    {
                        AnsiConsole.MarkupLine("[green]✓ Game is up to date and all files are intact.[/]");
                        return 0;
                    }
                    string reason = buildMismatch
                        ? $"build {localBuild} -> {steamBuild}"
                        : $"{verifyResult.MissingCount + verifyResult.CorruptCount} file(s) corrupt/missing";
                    AnsiConsole.MarkupLine($"\n[bold yellow]Updating ({reason})...[/]\n");
                }
            }
            else
            {
                if (steamBuild > 0 || localBuild > 0)
                    ShowBuildInfo(_currentSession, localBuild, steamBuild);
            }

            ui?.ShowStatus(
                $"✓ {_currentSession.AppName}  ·  " +
                $"{_currentSession.Depots.Count} depot(s)  ·  {totalFiles:N0} files  ·  " +
                $"{totalBytes / 1_073_741_824.0:F1} GB");

            _engine = new DownloadEngine(_currentSession, steam, _cts.Token);

            if (ui != null)
            {
                ui.SetAppName(_currentSession.AppName);
                await ui.RunDownloadWithProgressAsync(_engine, _currentSession);
            }
            else
            {
                await _engine.RunAsync();
            }

            if (_cts.Token.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⏸ Paused. Resume with:[/]\n" +
                    $"[grey]  --app {_currentSession.AppId} " +
                    $"--output \"{EscapeMarkup(_currentSession.OutputDir)}\" --resume[/]");
                return 2;
            }

            // Record in DB on success
            if (!_currentSession.WasCancelled)
            {
                long sz = _currentSession.Depots.Sum(d => d.Files.Sum(f => (long)f.Size));
                int  fc = _currentSession.Depots.Sum(d => d.Files.Count);
                LocalDatabase.Instance.RecordDownload(
                    _currentSession.AppId,
                    _currentSession.AppName,
                    _currentSession.OutputDir,
                    _currentSession.Depots.ToDictionary(d => d.DepotId, d => d.ManifestId),
                    buildId:        steamBuild,
                    totalSizeBytes: sz,
                    fileCount:      fc,
                    branch:         opts.Branch ?? "public",
                    os:             opts.Os);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"Fatal: {ex.Message}");
            if (debug) AnsiConsole.WriteException(ex);
            return 1;
        }
        finally { _cts?.Dispose(); }
    }

    // ─── --games ──────────────────────────────────────────────────────────────

    private static int ShowGames()
    {
        Logger.QuietMode = false;
        var records = LocalDatabase.Instance.GetAllGameRecords();

        if (records.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No games in database yet.[/]");
            AnsiConsole.MarkupLine("[grey]Download a game first — it'll appear here automatically.[/]");
            return 0;
        }

        var t = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("AppID")
            .AddColumn("Name")
            .AddColumn("Build")
            .AddColumn("Size")
            .AddColumn("Files")
            .AddColumn("Downloaded")
            .AddColumn("Status")
            .AddColumn("Path");

        foreach (var rec in records.OrderByDescending(x => x.LastDownloadedAt))
        {
            string build  = rec.BuildId > 0 ? rec.BuildId.ToString() : "[grey]—[/]";
            string size   = rec.TotalSizeBytes > 0
                ? $"{rec.TotalSizeBytes / 1_073_741_824.0:F1} GB" : "[grey]—[/]";
            string files  = rec.FileCount > 0 ? rec.FileCount.ToString() : "[grey]—[/]";
            string status = rec.VerifyStatus == "ok"      ? "[green]✓ OK[/]"      :
                            rec.VerifyStatus == "corrupt"  ? "[red]✗ Corrupt[/]"  :
                            rec.LastVerifiedAt.HasValue    ? "[grey]verified[/]"   :
                                                             "[grey]not verified[/]";
            t.AddRow(
                rec.AppId.ToString(),
                EscapeMarkup(rec.Name),
                build,
                size,
                files,
                rec.LastDownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                status,
                EscapeMarkup(rec.InstallPath));
        }

        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"\n[grey]{records.Count} game(s) total[/]");
        return 0;
    }

    // ─── --updates ────────────────────────────────────────────────────────────

    private static async Task<int> RunUpdateCheck(uint appId, string? apiKey)
    {
        Logger.QuietMode = false;
        var checker = new UpdateChecker(apiKey);
        var result  = await checker.CheckAsync(appId);
        UpdateChecker.PrintReport(new[] { result });
        return result.UpdateAvailable ? 1 : 0;
    }

    private static async Task<int> RunUpdateCheckAll(string? apiKey)
    {
        Logger.QuietMode = false;
        var checker = new UpdateChecker(apiKey);
        var results = await checker.CheckAllAsync();
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No games in database to check.[/]");
            return 0;
        }
        UpdateChecker.PrintReport(results);
        return results.Any(r => r.UpdateAvailable) ? 1 : 0;
    }

    // ─── Auth ─────────────────────────────────────────────────────────────────

    private static async Task<bool> AuthAsync(
        SteamSession steam, DownloadOptions opts, TerminalUI? ui)
    {
        if (string.IsNullOrEmpty(opts.Username))
            return await steam.ConnectAnonymousAsync();

        // Load saved credentials, fall back to baked-in .env values
        var creds = CredentialManager.Load(opts.Username);
        opts.Password ??= creds?.Password;

        // Prompt if still no password
        if (string.IsNullOrEmpty(opts.Password))
        {
            opts.Password = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Steam password:[/]")
                    .PromptStyle("red").Secret());
        }

        bool ok = await steam.ConnectAndLoginAsync(
            opts.Username, opts.Password,
            useQr: opts.UseQr,
            rememberPassword: opts.RememberPassword);

        if (ok && opts.RememberPassword)
            CredentialManager.Save(opts.Username, opts.Password);
        return ok;
    }

    // ─── Session builder ──────────────────────────────────────────────────────

    private static async Task<DownloadSession?> BuildSessionAsync(
        SteamSession steam, DownloadOptions opts)
    {
        var builder = new DownloadSessionBuilder(steam, opts);
        string name = await builder.GetAppNameAsync(opts.AppId);
        string safe = FileUtils.SanitizeFileName($"{opts.AppId}_{name}");
        if (string.IsNullOrWhiteSpace(safe)) safe = opts.AppId.ToString();
        return await builder.BuildAsync(Path.Combine(opts.OutputDir, safe));
    }

    // ─── Misc commands ────────────────────────────────────────────────────────

    private static void HandlePause()
    {
        if (_cts is { IsCancellationRequested: false })
            { _cts.Cancel(); AnsiConsole.MarkupLine("[yellow]⏸ Paused[/]"); }
        else
            AnsiConsole.MarkupLine("[yellow]ℹ No active download[/]");
    }

    private static void ShowStatus()
    {
        if (_engine == null || _currentSession == null)
            { AnsiConsole.MarkupLine("[yellow]ℹ No active download[/]"); return; }

        var s = _engine.GetStatistics();
        var t = new Table().Border(TableBorder.Rounded)
            .AddColumn("Metric").AddColumn("Value");
        t.AddRow("App",       $"{_currentSession.AppName} ({_currentSession.AppId})");
        t.AddRow("Progress",  $"{s.DownloadedMB:F1} / {s.TotalMB:F1} MB  ({s.Percent:F1}%)");
        t.AddRow("Speed",     $"{s.SpeedMBps:F2} MB/s");
        t.AddRow("Chunks",    $"{s.CompletedChunks} / {s.TotalChunks}");
        t.AddRow("Status",    s.IsCompleted ? "✓ Complete" : s.IsPaused ? "⏸ Paused" : "⬇ Running");
        AnsiConsole.Write(t);
    }

    // ─── Build info display ───────────────────────────────────────────────────

    private static void ShowBuildInfo(DownloadSession session, uint localBuild, uint steamBuild)
    {
        AnsiConsole.WriteLine();

        string appName = EscapeMarkup(session.AppName);

        if (localBuild > 0 && steamBuild > 0 && localBuild != steamBuild)
        {
            AnsiConsole.MarkupLine($"  [bold]{appName}[/]");
            AnsiConsole.MarkupLine($"  [grey]Local build:[/]   [yellow]{localBuild}[/]");
            AnsiConsole.MarkupLine($"  [grey]Steam build:[/]   [green]{steamBuild}[/]  [bold cyan]<- UPDATE AVAILABLE[/]");
        }
        else if (steamBuild > 0)
        {
            string buildStr = localBuild > 0 && localBuild == steamBuild
                ? $"[green]{steamBuild}  up to date[/]"
                : $"[cyan]{steamBuild}[/]";
            AnsiConsole.MarkupLine($"  [bold]{appName}[/]  ·  Build {buildStr}");
        }
        else if (localBuild > 0)
        {
            AnsiConsole.MarkupLine($"  [bold]{appName}[/]  ·  Local build [yellow]{localBuild}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [bold]{appName}[/]");
        }

        AnsiConsole.WriteLine();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static DownloadOptions BuildOptions(ParseResult r) => new()
    {
        AppId            = r.GetValueForOption(OptApp),
        DepotId          = r.GetValueForOption(OptDepot),
        ManifestId       = r.GetValueForOption(OptManifest),
        Branch           = r.GetValueForOption(OptBranch) ?? "public",
        BranchPassword   = r.GetValueForOption(OptBranchPassword),
        Username         = r.GetValueForOption(OptUsername),
        Password         = r.GetValueForOption(OptPassword),
        RememberPassword = r.GetValueForOption(OptRememberPass),
        UseQr            = r.GetValueForOption(OptQr),
        SharedSecret     = r.GetValueForOption(OptSharedSecret),
        DepotKeysFile    = r.GetValueForOption(OptDepotKeys),
        ManifestFile     = r.GetValueForOption(OptManifestFile),
        AppToken         = r.GetValueForOption(OptAppToken),
        PackageToken     = r.GetValueForOption(OptPackageToken),
        OutputDir        = r.GetValueForOption(OptOutput) ?? Environment.CurrentDirectory,
        FileListPath     = r.GetValueForOption(OptFileList),
        Validate         = r.GetValueForOption(OptValidate),
        MaxDownloads     = EmbeddedConfig.ResolveWorkers(r.GetValueForOption(OptMaxDownloads)),
        FallbackWorkers  = r.GetValueForOption(OptFallbackWorkers),
        CellId           = r.GetValueForOption(OptCellId) ?? (EmbeddedConfig.DefaultCdnRegion.HasValue ? EmbeddedConfig.DefaultCdnRegion : null),
        LoginId          = r.GetValueForOption(OptLoginId),
        Os               = r.GetValueForOption(OptOs) ?? GetCurrentOS(),
        OsArch           = r.GetValueForOption(OptOsArch) ?? GetCurrentArch(),
        Language         = r.GetValueForOption(OptLanguage) ?? "english",
        AllPlatforms     = r.GetValueForOption(OptAllPlatforms),
        AllArchs         = r.GetValueForOption(OptAllArchs),
        AllLanguages     = r.GetValueForOption(OptAllLanguages),
        LowViolence      = r.GetValueForOption(OptLowViolence),
        PubFileId        = r.GetValueForOption(OptPubFile),
        UgcId            = r.GetValueForOption(OptUgc),
        ManifestOnly     = r.GetValueForOption(OptManifestOnly),
        Debug            = r.GetValueForOption(OptDebug),
        ApiKey           = EmbeddedConfig.ResolveApiKey(r.GetValueForOption(OptApiKey)),
        Pause            = r.GetValueForOption(OptPause),
        ShowStatus       = r.GetValueForOption(OptStatus),
        TerminalUi       = r.GetValueForOption(OptTerminalUi),
        VerifyIntegrity  = r.GetValueForOption(OptVerify_),
        UpdateMode       = r.GetValueForOption(OptUpdate_),
    };

    private static string EscapeMarkup(string s) =>
        s.Replace("[", "[[").Replace("]", "]]");

    private static string GetCurrentOS()   => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    private static string GetCurrentArch() => Environment.Is64BitOperatingSystem ? "64" : "32";
}
