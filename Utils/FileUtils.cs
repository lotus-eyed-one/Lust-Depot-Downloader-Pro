using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using LustsDepotDownloaderPro.Models;

namespace LustsDepotDownloaderPro.Utils;

public static class FileUtils
{
    public static string SanitizeFileName(string fileName)
    {
        // Remove invalid characters
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder();

        foreach (char c in fileName)
        {
            if (!invalid.Contains(c))
            {
                sanitized.Append(c);
            }
            else
            {
                sanitized.Append('_');
            }
        }

        // Limit length
        string result = sanitized.ToString();
        if (result.Length > 200)
        {
            result = result.Substring(0, 200);
        }

        return result;
    }

    public static List<string> LoadFileList(string fileListPath)
    {
        var filters = new List<string>();

        if (!File.Exists(fileListPath))
        {
            Logger.Warn($"File list not found: {fileListPath}");
            return filters;
        }

        var lines = File.ReadAllLines(fileListPath);
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
            {
                filters.Add(trimmed);
            }
        }

        Logger.Info($"Loaded {filters.Count} file filters");
        return filters;
    }
}

public static class FilterMatcher
{
    public static bool Matches(string fileName, string filter)
    {
        // Check if it's a regex filter
        if (filter.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            string pattern = filter.Substring(6);
            try
            {
                return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Wildcard matching
        string regexPattern = "^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}

public static class CredentialManager
{
    private static readonly string _credentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LustsDepotDownloader",
        "credentials.json");

    public static void Save(string username, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_credentialsPath)!);

            var credentials = LoadAll();
            credentials[username] = new UserCredentials
            {
                Username = username,
                Password = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
                LastUsed = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(credentials, Formatting.Indented);
            File.WriteAllText(_credentialsPath, json);

            Logger.Debug($"Credentials saved for user: {username}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to save credentials: {ex.Message}");
        }
    }

    public static void SaveRefreshToken(string username, string refreshToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_credentialsPath)!);

            var credentials = LoadAll();
            
            if (!credentials.ContainsKey(username))
            {
                credentials[username] = new UserCredentials { Username = username };
            }

            credentials[username].RefreshToken = refreshToken;
            credentials[username].LastUsed = DateTime.UtcNow;

            var json = JsonConvert.SerializeObject(credentials, Formatting.Indented);
            File.WriteAllText(_credentialsPath, json);

            Logger.Debug($"Refresh token saved for user: {username}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to save refresh token: {ex.Message}");
        }
    }

    public static UserCredentials? Load(string? username)
    {
        if (string.IsNullOrEmpty(username))
            return null;

        try
        {
            var credentials = LoadAll();
            
            if (credentials.TryGetValue(username, out var cred))
            {
                // Decode password
                if (!string.IsNullOrEmpty(cred.Password))
                {
                    cred.Password = Encoding.UTF8.GetString(Convert.FromBase64String(cred.Password));
                }
                return cred;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to load credentials: {ex.Message}");
        }

        return null;
    }

    private static Dictionary<string, UserCredentials> LoadAll()
    {
        if (!File.Exists(_credentialsPath))
            return new Dictionary<string, UserCredentials>();

        try
        {
            var json = File.ReadAllText(_credentialsPath);
            return JsonConvert.DeserializeObject<Dictionary<string, UserCredentials>>(json) 
                   ?? new Dictionary<string, UserCredentials>();
        }
        catch
        {
            return new Dictionary<string, UserCredentials>();
        }
    }
}

public class UserCredentials
{
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime LastUsed { get; set; }
}



public static class ProcessRunner
{
    public static async Task<int> RunAsync(string command, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return -1;

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
