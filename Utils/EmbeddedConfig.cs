namespace LustsDepotDownloaderPro.Utils;

/// <summary>
/// Compile-time constants baked in from .env at build time.
/// Values are injected by EmbeddedConfig.Generated.cs (auto-generated from .env on each build).
/// </summary>
public static partial class EmbeddedConfig
{
    // ── GitHub tokens ─────────────────────────────────────────────────────────
    // Both are used simultaneously — each has its own 5000 req/hr limit.
    // Two tokens = 10,000 req/hr. Either can be empty.
    public static string GitHubApiKeyPat     => _GitHubApiKeyPat;
    public static string GitHubApiKeyClassic => _GitHubApiKeyClassic;

    // Combined: returns all non-empty tokens for the dual-token pool
    public static string[] GitHubTokens => new[] { _GitHubApiKeyPat, _GitHubApiKeyClassic }
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .ToArray();

    // ── Steam ─────────────────────────────────────────────────────────────────
    public static string SteamWebApiKey     => _SteamWebApiKey;
    public static string SteamCdnToken      => _SteamCdnToken;
    public static string SteamSharedSecret  => _SteamSharedSecret;
    public static string SteamIdentitySecret => _SteamIdentitySecret;
    public static string SteamRefreshToken  => _SteamRefreshToken;
    public static string ManifestHubApiKey  => _ManifestHubApiKey;
    public static string DepotBoxApiKey     => _DepotBoxApiKey;

    // ── Defaults ──────────────────────────────────────────────────────────────
    public static int?   DefaultCdnRegion   => _DefaultCdnRegion;
    public static int    DefaultWorkers     => _DefaultWorkers;
    public static string AppVersion         => _AppVersion;

    // ── Merge helpers (CLI arg wins over baked-in value) ─────────────────────

    /// <summary>Returns the first non-empty value from: CLI arg → PAT → Classic → null.</summary>
    public static string? ResolveApiKey(string? cliValue) =>
        NonEmpty(cliValue) ?? NonEmpty(GitHubApiKeyPat) ?? NonEmpty(GitHubApiKeyClassic);

    public static string? ResolveSharedSecret(string? cliValue) =>
        NonEmpty(cliValue) ?? NonEmpty(SteamSharedSecret);

    public static string? ResolveRefreshToken(string? cliValue) =>
        NonEmpty(cliValue) ?? NonEmpty(SteamRefreshToken);

    public static int ResolveCdnRegion(int? cliValue) =>
        cliValue ?? DefaultCdnRegion ?? 0;

    public static int ResolveWorkers(int cliValue) =>
        cliValue > 0 ? cliValue : DefaultWorkers;

    private static string? NonEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    // ── Backing fields — overridden by EmbeddedConfig.Generated.cs ───────────
    internal static string _GitHubApiKeyPat     = "";
    internal static string _GitHubApiKeyClassic = "";
    internal static string _SteamWebApiKey      = "";
    internal static string _SteamCdnToken       = "";
    internal static string _SteamSharedSecret   = "";
    internal static string _SteamIdentitySecret = "";
    internal static string _SteamRefreshToken   = "";
    internal static string _ManifestHubApiKey  = "";
    internal static string _DepotBoxApiKey     = "";
    internal static int?   _DefaultCdnRegion    = null;
    internal static int    _DefaultWorkers      = 8;
    internal static string _AppVersion          = "1.5.0";
}
