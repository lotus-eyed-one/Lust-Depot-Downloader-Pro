using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LustsDepotDownloaderPro.Models;
using LustsDepotDownloaderPro.Utils;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// Scans Steam library folders and reads AppManifest ACF files to detect
/// all locally installed games.
///
/// Equivalent to Cirno's fetch_installed_games / fetch_steam_game_installed_games commands.
///
/// Flow:
///   1. Find Steam install path (registry on Windows, known dirs on Linux/macOS)
///   2. Parse steamapps/libraryfolders.vdf for additional library paths
///   3. Glob steamapps/appmanifest_*.acf in each library
///   4. Parse each ACF for: AppID, name, installdir, buildid, SizeOnDisk, StateFlags
/// </summary>
public static class SteamLibraryScanner
{
    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all installed Steam games across all library folders.
    /// </summary>
    public static List<InstalledGame> ScanInstalledGames()
    {
        var games = new List<InstalledGame>();

        var libraryPaths = GetSteamLibraryPaths();
        if (libraryPaths.Count == 0)
        {
            Logger.Warn("Steam installation not found or no library paths detected.");
            return games;
        }

        Logger.Debug($"Found {libraryPaths.Count} Steam library path(s)");

        foreach (var libPath in libraryPaths)
        {
            var steamappsDir = Path.Combine(libPath, "steamapps");
            if (!Directory.Exists(steamappsDir)) continue;

            foreach (var acf in Directory.GetFiles(steamappsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var game = ParseAcf(acf, libPath);
                    if (game != null) games.Add(game);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"ACF parse error {Path.GetFileName(acf)}: {ex.Message}");
                }
            }
        }

        Logger.Info($"Found {games.Count} installed Steam game(s)");
        return games;
    }

    /// <summary>
    /// Returns all library folder paths (including the default Steam library).
    /// </summary>
    public static List<string> GetSteamLibraryPaths()
    {
        var paths = new List<string>();

        string? steamRoot = FindSteamRoot();
        if (steamRoot == null) return paths;

        paths.Add(steamRoot);

        // Parse libraryfolders.vdf for additional library paths
        string libFolders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libFolders))
        {
            var extra = ParseLibraryFoldersVdf(libFolders);
            foreach (var p in extra)
                if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    paths.Add(p);
        }

        return paths.Where(Directory.Exists).ToList();
    }

    // ─── Steam root detection ─────────────────────────────────────────────────

    private static string? FindSteamRoot()
    {
        if (OperatingSystem.IsWindows())
            return FindSteamRootWindows();
        if (OperatingSystem.IsLinux())
            return FindSteamRootLinux();
        if (OperatingSystem.IsMacOS())
            return FindSteamRootMac();
        return null;
    }

    private static string? FindSteamRootWindows()
    {
        // Try registry first (most reliable)
        string[] registryKeys =
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
        };

        foreach (var key in registryKeys)
        {
            try
            {
                // Use Microsoft.Win32.Registry via reflection to stay cross-platform compilable
                var val = ReadRegistry(key, "InstallPath") ?? ReadRegistry(key, "SteamPath");
                if (val != null && Directory.Exists(val))
                {
                    Logger.Debug($"Steam found via registry: {val}");
                    return val;
                }
            }
            catch { }
        }

        // Fallback: common install paths
        string[] fallbacks =
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
        };
        return fallbacks.FirstOrDefault(Directory.Exists);
    }

    private static string? FindSteamRootLinux()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".local", "share", "Steam"),
            "/usr/share/steam",
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? FindSteamRootMac()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, "Library", "Application Support", "Steam"),
            "/Applications/Steam.app/Contents/MacOS",
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Reads a Windows registry value. Only works on Windows; returns null on other platforms.
    /// Using reflection so the code compiles on Linux/macOS too.
    /// </summary>
    private static string? ReadRegistry(string keyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Split "HKEY_LOCAL_MACHINE\SOFTWARE\..." into hive + subkey
            int sep = keyPath.IndexOf('\\');
            string hiveName = keyPath[..sep];
            string subKey   = keyPath[(sep + 1)..];

            // Load Microsoft.Win32 dynamically
            var regType   = Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry");
            if (regType == null)
                regType = Type.GetType("Microsoft.Win32.Registry");
            if (regType == null) return null;

            var hiveProp  = regType.GetProperty(hiveName switch
            {
                "HKEY_LOCAL_MACHINE" => "LocalMachine",
                "HKEY_CURRENT_USER"  => "CurrentUser",
                _ => ""
            });
            if (hiveProp == null) return null;

            dynamic? hive   = hiveProp.GetValue(null);
            dynamic? key    = hive?.OpenSubKey(subKey);
            return key?.GetValue(valueName)?.ToString();
        }
        catch { return null; }
    }

    // ─── libraryfolders.vdf parser ────────────────────────────────────────────

    private static List<string> ParseLibraryFoldersVdf(string path)
    {
        var paths = new List<string>();
        try
        {
            string content = File.ReadAllText(path);
            // Modern format: "path"  "/some/path/to/library"
            var rx = new Regex(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(content))
            {
                string p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(p)) paths.Add(p);
            }

            // Legacy format: "1"  "/path"  (integer keys)
            if (paths.Count == 0)
            {
                var legacyRx = new Regex(@"""[0-9]+""\s+""([^""]+)""");
                foreach (Match m in legacyRx.Matches(content))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p)) paths.Add(p);
                }
            }
        }
        catch (Exception ex) { Logger.Debug($"libraryfolders.vdf parse: {ex.Message}"); }
        return paths;
    }

    // ─── ACF parser ───────────────────────────────────────────────────────────

    private static InstalledGame? ParseAcf(string acfPath, string libraryRoot)
    {
        string content = File.ReadAllText(acfPath);

        string? GetVal(string key)
        {
            var m = Regex.Match(content,
                $@"""{Regex.Escape(key)}""\s+""([^""]*)""",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        if (!uint.TryParse(GetVal("appid"), out uint appId)) return null;

        string name       = GetVal("name") ?? $"App_{appId}";
        string installDir = GetVal("installdir") ?? "";
        string library    = Path.Combine(libraryRoot, "steamapps", "common", installDir);

        ulong.TryParse(GetVal("buildid"),   out ulong buildId);
        long.TryParse(GetVal("SizeOnDisk"), out long sizeOnDisk);
        int.TryParse(GetVal("StateFlags"),  out int stateFlags);

        DateTime? lastUpdated = null;
        if (long.TryParse(GetVal("LastUpdated"), out long ts) && ts > 0)
            lastUpdated = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

        return new InstalledGame
        {
            AppId       = appId,
            Name        = name,
            InstallDir  = library,
            LibraryPath = libraryRoot,
            SizeOnDisk  = sizeOnDisk,
            BuildId     = buildId,
            LastUpdated = lastUpdated,
            StateFlags  = stateFlags,
        };
    }
}
