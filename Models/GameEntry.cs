using Newtonsoft.Json;

namespace LustsDepotDownloaderPro.Models;

/// <summary>
/// Mirrors the Cirno downloader's CirnoGameList / CirnoGame / CirnoGameConfig / ConfigDepot model.
///
/// CirnoGameList  (1 field)  → List of games
/// CirnoGame      (7 fields) → AppId, Name, IsNew, Thumbnail, Configs, Changelog, SteamMeta
/// CirnoGameConfig(4 fields) → Name, Paths, Depots, Preload
/// ConfigDepot    (3 fields) → DepotId, ManifestId, Key
/// </summary>

// ─── CirnoGameList ────────────────────────────────────────────────────────────

public class CirnoGameList
{
    [JsonProperty("list")]
    public List<CirnoGame> List { get; set; } = new();
}

// ─── CirnoGame ────────────────────────────────────────────────────────────────

public class CirnoGame
{
    [JsonProperty("app_id")]
    public uint AppId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("is_new")]
    public bool IsNew { get; set; }

    [JsonProperty("thumbnail")]
    public string Thumbnail { get; set; } = "";

    [JsonProperty("configs")]
    public List<CirnoGameConfig> Configs { get; set; } = new();

    [JsonProperty("changelog")]
    public string Changelog { get; set; } = "";

    [JsonProperty("steam_meta")]
    public SteamGameMeta? SteamMeta { get; set; }

    public override string ToString() => $"[{AppId}] {Name}";
}

// ─── SteamGameMeta (sdmeta equivalent) ───────────────────────────────────────

public class SteamGameMeta
{
    [JsonProperty("developers")]
    public List<string> Developers { get; set; } = new();

    [JsonProperty("publishers")]
    public List<string> Publishers { get; set; } = new();

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("metacritic_score")]
    public int? MetacriticScore { get; set; }

    [JsonProperty("user_score")]
    public double? UserScore { get; set; }

    [JsonProperty("release_date")]
    public string ReleaseDate { get; set; } = "";

    [JsonProperty("genres")]
    public List<string> Genres { get; set; } = new();
}

// ─── CirnoGameConfig ─────────────────────────────────────────────────────────

public class CirnoGameConfig
{
    /// <summary>Human-readable config name, e.g. "Windows x64" or "Base Game".</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    /// <summary>Relative install paths for this config within the output dir.</summary>
    [JsonProperty("paths")]
    public List<string> Paths { get; set; } = new();

    /// <summary>Depot list with manifest IDs and decryption keys.</summary>
    [JsonProperty("depots")]
    public List<ConfigDepot> Depots { get; set; } = new();

    /// <summary>If true, this config supports preloading before release.</summary>
    [JsonProperty("preload")]
    public bool Preload { get; set; }
}

// ─── ConfigDepot ─────────────────────────────────────────────────────────────

public class ConfigDepot
{
    [JsonProperty("depot_id")]
    public uint DepotId { get; set; }

    [JsonProperty("manifest_id")]
    public ulong ManifestId { get; set; }

    /// <summary>Hex-encoded AES depot decryption key (32 bytes = 64 hex chars). May be null for free/anon depots.</summary>
    [JsonProperty("key")]
    public string? Key { get; set; }

    public byte[]? KeyBytes => Key is { Length: 64 }
        ? Convert.FromHexString(Key)
        : null;
}

// ─── Download record (Cirno Download struct, 5 fields) ───────────────────────

public class DownloadRecord
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonProperty("app_id")]
    public uint AppId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("status")]
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    [JsonProperty("progress")]
    public double Progress { get; set; }   // 0.0 – 100.0

    [JsonProperty("output_dir")]
    public string OutputDir { get; set; } = "";

    [JsonProperty("started_at")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    [JsonProperty("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonProperty("depot_manifest_ids")]
    public Dictionary<uint, ulong> DepotManifestIds { get; set; } = new();
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Complete,
    Failed,
    Cancelled,
    UpdateAvailable
}

// ─── Installed game (from Steam ACF scanner) ─────────────────────────────────

public class InstalledGame
{
    [JsonProperty("app_id")]
    public uint AppId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("install_dir")]
    public string InstallDir { get; set; } = "";

    [JsonProperty("library_path")]
    public string LibraryPath { get; set; } = "";

    [JsonProperty("size_on_disk")]
    public long SizeOnDisk { get; set; }

    [JsonProperty("build_id")]
    public ulong BuildId { get; set; }

    [JsonProperty("last_updated")]
    public DateTime? LastUpdated { get; set; }

    [JsonProperty("state_flags")]
    public int StateFlags { get; set; }

    /// <summary>StateFlags & 4 means fully installed.</summary>
    public bool IsFullyInstalled => (StateFlags & 4) == 4;
}

// ─── Update check result ──────────────────────────────────────────────────────

public class UpdateCheckResult
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public Dictionary<uint, (ulong Local, ulong Remote)> ChangedDepots { get; set; } = new();
    public string Message { get; set; } = "";
}
