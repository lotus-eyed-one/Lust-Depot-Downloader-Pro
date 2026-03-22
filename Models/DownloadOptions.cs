namespace LustsDepotDownloaderPro.Models;

public class DownloadOptions
{
    // App and Depot
    public uint AppId { get; set; }
    public uint? DepotId { get; set; }
    public ulong? ManifestId { get; set; }
    
    // Branch
    public string Branch { get; set; } = "public";
    public string? BranchPassword { get; set; }
    
    // Authentication
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool RememberPassword { get; set; }
    public bool UseQr { get; set; }
    
    // Keys and Tokens
    public string? DepotKeysFile { get; set; }
    public string? ManifestFile { get; set; }
    public string? AppToken { get; set; }
    public string? PackageToken { get; set; }
    
    // Output
    public string OutputDir { get; set; } = Environment.CurrentDirectory;
    public string? FileListPath { get; set; }
    
    // Options
    public bool Validate { get; set; }
    public int MaxDownloads { get; set; } = 8;

    /// <summary>
    /// How many of the total workers are fallback (direct-HTTP) workers.
    /// Default: -1 = auto (25% of MaxDownloads, minimum 1).
    /// Set to 0 to disable fallback workers entirely (primary only).
    /// Set to MaxDownloads to use fallback workers only.
    /// </summary>
    public int FallbackWorkers { get; set; } = -1;
    public int? CellId { get; set; }
    public uint? LoginId { get; set; }
    
    // Platform
    public string Os { get; set; } = "windows";
    public string OsArch { get; set; } = "64";
    public string Language { get; set; } = "english";
    public bool AllPlatforms { get; set; }
    public bool AllArchs { get; set; }
    public bool AllLanguages { get; set; }
    public bool LowViolence { get; set; }
    
    // Workshop
    public ulong? PubFileId { get; set; }
    public ulong? UgcId { get; set; }
    
    // Modes
    public bool ManifestOnly { get; set; }
    public bool Debug { get; set; }
    
    // API
    public string? ApiKey { get; set; }
    
    // Steam Guard auto-TOTP (base64 shared_secret from maFile)
    public string? SharedSecret { get; set; }

    // Control
    public bool Pause { get; set; }
    public bool Resume { get; set; }
    public bool ShowStatus { get; set; }
    public bool TerminalUi { get; set; } = true;

    /// <summary>Verify existing files against manifest before/instead of downloading.</summary>
    public bool VerifyIntegrity { get; set; }

    /// <summary>Update mode: check build, verify files, re-download only changed/missing.</summary>
    public bool UpdateMode { get; set; }
}
