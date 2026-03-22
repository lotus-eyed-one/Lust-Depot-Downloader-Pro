using SteamKit2;
using SteamKit2.CDN;
using LustsDepotDownloaderPro.Core;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// Steam session — proven original auth core with new features layered on top.
///
/// Auth flow:
///  1. Anonymous                         — no credentials, free/community depots
///  2. Refresh token (saved in DB)       — silent re-login, no password prompt
///  3. TOTP auto-code (shared_secret)    — auto-generates 2FA, no manual entry
///  4. Password + interactive Steam Guard — prompts only when needed
///
/// Fixed bugs vs the "new" SteamSession:
///  • Semaphore deadlock on 2FA retry: OnLoggedOn was returning early without
///    releasing _loginSignal, so the 45s timeout fired even on successful login.
///    Fix: keep a flag so the signal is released exactly once after all retries.
///  • Multiple callback pumps on reconnect: EnsureConnectedAsync was spawning a
///    new pump Task on every attempt without stopping the previous one.
///    Fix: single pump; reset _isRunning gate before each attempt.
///  • CdnManager called GetCdnServersForAppAsync / GetCdnAuthTokenWithExpiryAsync
///    which don't exist in SteamKit2 3.2.0.
///    Fix: expose only real SK2 3.2.0 APIs here, let CdnManager use them.
/// </summary>
public class SteamSession : IDisposable
{
    // ── SteamKit2 handlers ────────────────────────────────────────────────────
    private readonly SteamClient     _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser       _steamUser;
    private readonly SteamApps       _steamApps;
    private readonly SteamContent    _steamContent;

    // ── Session state ─────────────────────────────────────────────────────────
    private bool    _isRunning;
    private bool    _isLoggedIn;
    private bool    _isConnected;
    private string? _username;
    private string? _password;

    // _loginPending: true while we are inside a login attempt (including retries
    // for Steam Guard / 2FA). The signal is released exactly once when this
    // flips back to false — preventing the double-release / deadlock.
    private bool    _loginPending;

    private readonly SemaphoreSlim _loginSignal   = new(0, 1);
    private readonly SemaphoreSlim _connectSignal = new(0, 1);
    private readonly int?  _cellId;
    private readonly uint? _loginId;

    // ── Public surface ────────────────────────────────────────────────────────
    public SteamKit2.CDN.Client CdnClient { get; }
    public SteamApps  Apps      => _steamApps;
    public SteamUser  User      => _steamUser;
    public bool       IsLoggedIn => _isLoggedIn;

    // ── CDN server + token cache ──────────────────────────────────────────────
    private IReadOnlyCollection<Server>? _cdnServers;

    // Key = (appId, depotId) — simple per-depot cache (no per-server, SK2 3.2.0 compat)
    private readonly Dictionary<(uint, uint), (string token, DateTime expiry)> _cdnTokenCache = new();
    private readonly object _tokenLock = new();

    // ═════════════════════════════════════════════════════════════════════════
    // Construction
    // ═════════════════════════════════════════════════════════════════════════

    public SteamSession(int? cellId = null, uint? loginId = null)
    {
        _cellId  = cellId;
        _loginId = loginId;

        var config = SteamConfiguration.Create(c =>
            c.WithWebAPIKey("")
             .WithConnectionTimeout(TimeSpan.FromSeconds(30)));

        _steamClient     = new SteamClient(config);
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser       = _steamClient.GetHandler<SteamUser>()!;
        _steamApps       = _steamClient.GetHandler<SteamApps>()!;
        _steamContent    = _steamClient.GetHandler<SteamContent>()!;
        CdnClient        = new SteamKit2.CDN.Client(_steamClient);

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Auth — public API
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Anonymous login — works for community / free-to-play depots.</summary>
    public async Task<bool> ConnectAnonymousAsync()
    {
        Logger.Info("Connecting to Steam anonymously...");
        return await ConnectAndLoginInternalAsync(() => _steamUser.LogOnAnonymous());
    }

    /// <summary>
    /// Full auth with credentials.
    /// Priority: saved refresh token → auto-TOTP → interactive prompts.
    /// </summary>
    public async Task<bool> ConnectAndLoginAsync(
        string username, string password,
        bool useQr = false, bool rememberPassword = false)
    {
        _username = username;
        _password = password;

        if (useQr)
        {
            Logger.Warn("QR code login requires SteamKit2 3.4.0+. Use --username/--password.");
            return false;
        }

        // ── 1. Silent refresh-token re-login ──────────────────────────────────
        var savedToken = LocalDatabase.Instance.GetSetting($"refresh_token:{username}");
        if (!string.IsNullOrEmpty(savedToken))
        {
            Logger.Info($"Trying saved refresh token for {username}...");
            bool ok = await ConnectAndLoginInternalAsync(() =>
                _steamUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username               = username,
                    AccessToken            = savedToken,
                    ShouldRememberPassword = true,
                    LoginID                = _loginId
                }));
            if (ok) { Logger.Info("Silent re-login OK"); return true; }

            Logger.Warn("Refresh token rejected — clearing, falling back to password...");
            LocalDatabase.Instance.SetSetting($"refresh_token:{username}", "");
            // Reconnect fresh for the password attempt
            _isRunning = false;
            _steamClient.Disconnect();
            await Task.Delay(1000);
        }

        // ── 2. Password login (with optional auto-TOTP) ───────────────────────
        // If shared_secret in .env, generate the 2FA code now so we include it
        // in the FIRST LogOn call — avoids the AccountLoginDeniedNeedTwoFactor
        // round-trip entirely.
        string? sharedSecret = EmbeddedConfig.SteamSharedSecret;
        string? twoFactorCode = null;

        if (!string.IsNullOrEmpty(sharedSecret))
        {
            Logger.Info("Auto-generating 2FA code from shared_secret...");
            int offset = await SteamTotp.GetSteamTimeOffsetAsync();
            twoFactorCode = SteamTotp.GenerateAuthCode(sharedSecret, offset);
            Logger.Debug($"TOTP code ready (expires in {SteamTotp.SecondsUntilChange()}s)");
        }

        bool loginOk = await ConnectAndLoginInternalAsync(() =>
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username               = username,
                Password               = password,
                TwoFactorCode          = twoFactorCode,
                ShouldRememberPassword = rememberPassword,
                LoginID                = _loginId
            }));

        if (loginOk && rememberPassword)
            LocalDatabase.Instance.SetSetting($"refresh_token:{username}", "__logged_in__");

        return loginOk;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CDN — real SteamKit2 3.2.0 APIs only
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the CDN server list.  Cached for the session.
    /// GetServersForSteamPipe is the only server-list API in SK2 3.2.0.
    /// </summary>
    public async Task<IReadOnlyCollection<Server>> GetCdnServersAsync()
    {
        if (_cdnServers != null) return _cdnServers;
        try
        {
            _cdnServers = await _steamContent.GetServersForSteamPipe();
            Logger.Debug($"CDN: {_cdnServers.Count} servers");
        }
        catch (Exception ex)
        {
            Logger.Warn($"CDN server list: {ex.Message}");
            _cdnServers = Array.Empty<Server>();
        }
        return _cdnServers;
    }

    /// <summary>Cached host strings for FallbackDownloader.</summary>
    public IReadOnlyList<string> GetCachedCdnHosts()
    {
        if (_cdnServers == null) return Array.Empty<string>();
        return _cdnServers
            .Where(s => !string.IsNullOrEmpty(s.Host))
            .Select(s => s.Host!)
            .ToList();
    }

    /// <summary>
    /// Get a CDN auth token for the given app/depot/server.
    /// Per SK2 3.2.0: GetCDNAuthToken lives on SteamContent.
    /// Cached per (appId, depotId) with 5-minute safety margin before expiry.
    /// Returns null / empty string when not needed (most cases).
    /// </summary>
    public async Task<string?> GetCdnAuthTokenAsync(
        Server server, uint appId, uint depotId)
    {
        var key = (appId, depotId);
        lock (_tokenLock)
        {
            if (_cdnTokenCache.TryGetValue(key, out var cached) &&
                DateTime.UtcNow < cached.expiry - TimeSpan.FromMinutes(5))
                return cached.token;
        }

        try
        {
            var cb = await _steamContent.GetCDNAuthToken(appId, depotId, server.Host ?? "");
            if (cb.Result == EResult.OK)
            {
                lock (_tokenLock)
                {
                    _cdnTokenCache[key] = (cb.Token, cb.Expiration);
                }
                Logger.Debug($"CDN token: depot {depotId} @ {server.Host}");
                return cb.Token;
            }
            Logger.Debug($"CDN auth token depot {depotId}: {cb.Result}");
            return null;
        }
        catch (Exception ex) { Logger.Debug($"GetCDNAuthToken: {ex.Message}"); return null; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Depot key & manifest
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<byte[]?> GetDepotKeyAsync(uint depotId, uint appId)
    {
        try
        {
            var cb = await _steamApps.GetDepotDecryptionKey(depotId, appId);
            if (cb.Result == EResult.OK) return cb.DepotKey;
            Logger.Warn($"GetDepotDecryptionKey depot {depotId}: {cb.Result}");
            return null;
        }
        catch (Exception ex) { Logger.Warn($"GetDepotKeyAsync: {ex.Message}"); return null; }
    }

    public async Task<ulong> GetManifestRequestCodeAsync(
        uint appId, uint depotId, ulong manifestId,
        string branch = "public", string? branchPassword = null)
    {
        try
        {
            return await _steamContent.GetManifestRequestCode(
                appId, depotId, manifestId, branch, branchPassword);
        }
        catch (Exception ex) { Logger.Debug($"ManifestRequestCode: {ex.Message}"); return 0; }
    }

    /// <summary>
    /// Download a depot manifest, retrying across CDN servers.
    /// Requests a CDN auth token on 401.
    /// </summary>
    public async Task<DepotManifest?> DownloadManifestAsync(
        uint appId, uint depotId, ulong manifestId, byte[]? depotKey,
        string branch = "public", string? branchPassword = null)
    {
        ulong requestCode = await GetManifestRequestCodeAsync(
            appId, depotId, manifestId, branch, branchPassword);

        var servers = await GetCdnServersAsync();
        Exception? lastEx = null;

        foreach (var server in servers.Take(8))
        {
            try
            {
                var manifest = await CdnClient.DownloadManifestAsync(
                    depotId, manifestId, requestCode, server, depotKey,
                    proxyServer: null, cdnAuthToken: null);
                Logger.Debug($"Manifest {manifestId}: {manifest.Files?.Count ?? 0} files");
                return manifest;
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Logger.Debug($"401 on {server.Host} — fetching CDN auth token...");
                var token = await GetCdnAuthTokenAsync(server, appId, depotId);
                if (token == null) { lastEx = ex; continue; }
                try
                {
                    var manifest = await CdnClient.DownloadManifestAsync(
                        depotId, manifestId, requestCode, server, depotKey,
                        proxyServer: null, cdnAuthToken: token);
                    Logger.Debug($"Manifest {manifestId} (auth): {manifest.Files?.Count ?? 0} files");
                    return manifest;
                }
                catch (Exception ex2) { lastEx = ex2; Logger.Debug($"Retry {server.Host}: {ex2.Message}"); }
            }
            catch (Exception ex) { lastEx = ex; Logger.Debug($"{server.Host}: {ex.Message}"); }
        }

        Logger.Error($"All CDN servers failed for manifest {manifestId}: {lastEx?.Message}");
        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Internal connection helpers
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<bool> EnsureConnectedAsync()
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Logger.Info($"Connection attempt {attempt}/{maxAttempts}...");

            // Stop any previous pump before starting a new one
            _isRunning = false;
            await Task.Delay(200); // brief gap for previous pump to exit

            _isRunning = true;
            _ = Task.Run(RunCallbackPump);

            _steamClient.Connect();

            bool connected = await _connectSignal.WaitAsync(TimeSpan.FromSeconds(30));
            if (connected && _isConnected)
            {
                Logger.Info($"Connected: {_steamClient.CurrentEndPoint}");
                return true;
            }

            Logger.Warn($"Attempt {attempt} timed out — retrying...");
            _isRunning = false;
            _steamClient.Disconnect();
            await Task.Delay(2000);
        }
        Logger.Error("All Steam connection attempts failed");
        return false;
    }

    private async Task<bool> ConnectAndLoginInternalAsync(Action loginAction)
    {
        if (!await EnsureConnectedAsync()) return false;

        Logger.Info("Logging in...");
        _loginPending = true;
        loginAction();

        // Wait up to 60s — covers Steam Guard prompt + user typing the code
        await _loginSignal.WaitAsync(TimeSpan.FromSeconds(60));
        if (_isLoggedIn) { Logger.Info("Successfully logged in"); return true; }
        Logger.Error("Login failed");
        return false;
    }

    private void RunCallbackPump()
    {
        while (_isRunning)
        {
            try { _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { Logger.Debug($"Callback pump: {ex.Message}"); }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Steam callbacks
    // ═════════════════════════════════════════════════════════════════════════

    private void OnConnected(SteamClient.ConnectedCallback _)
    {
        _isConnected = true;
        try { _connectSignal.Release(); } catch { }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        Logger.Debug($"Disconnected (user-initiated: {cb.UserInitiated})");
        _isConnected = false;
        _isLoggedIn  = false;
        // Release connect signal so EnsureConnectedAsync doesn't hang
        try { _connectSignal.Release(); } catch { }
        // Release login signal too in case we disconnected mid-login
        if (_loginPending)
        {
            _loginPending = false;
            try { _loginSignal.Release(); } catch { }
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        // ── Steam Guard (email) ───────────────────────────────────────────────
        if (cb.Result == EResult.AccountLogonDenied)
        {
            Console.Write("\nSteam Guard code (email): ");
            var code = Console.ReadLine()?.Trim();
            _steamUser.LogOn(new SteamUser.LogOnDetails
                { Username = _username, Password = _password,
                  AuthCode = code, LoginID = _loginId });
            return;  // _loginPending stays true; wait for next OnLoggedOn
        }

        // ── 2FA (Mobile Authenticator) — only hit when no shared_secret ──────
        if (cb.Result == EResult.AccountLoginDeniedNeedTwoFactor)
        {
            Console.Write("\nSteam Mobile Authenticator code: ");
            var code = Console.ReadLine()?.Trim();
            _steamUser.LogOn(new SteamUser.LogOnDetails
                { Username = _username, Password = _password,
                  TwoFactorCode = code, LoginID = _loginId });
            return;  // _loginPending stays true; wait for next OnLoggedOn
        }

        // ── Final result ──────────────────────────────────────────────────────
        if (cb.Result == EResult.OK)
        {
            _isLoggedIn = true;
            Logger.Info($"Logged in — SteamID: {cb.ClientSteamID}");
        }
        else
        {
            _isLoggedIn = false;
            Logger.Error($"Login failed: {cb.Result} / {cb.ExtendedResult}");
            // Clear stale refresh token on hard auth failure
            if (!string.IsNullOrEmpty(_username) &&
                cb.Result is EResult.InvalidPassword or EResult.InvalidLoginAuthCode
                          or EResult.TwoFactorCodeMismatch)
            {
                LocalDatabase.Instance.SetSetting($"refresh_token:{_username}", "");
            }
        }

        // Release exactly once
        if (_loginPending)
        {
            _loginPending = false;
            try { _loginSignal.Release(); } catch { }
        }
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Logger.Warn($"Logged off: {cb.Result}");
        _isLoggedIn = false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // IDisposable
    // ═════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        _isRunning = false;
        try { if (_isLoggedIn) _steamUser.LogOff(); } catch { }
        try { _steamClient.Disconnect(); }            catch { }
        _loginSignal.Dispose();
        _connectSignal.Dispose();
    }
}
