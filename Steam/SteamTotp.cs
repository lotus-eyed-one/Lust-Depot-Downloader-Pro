using System.Security.Cryptography;
using System.Text;

namespace LustsDepotDownloaderPro.Steam;

/// <summary>
/// C# port of node-steam-totp (DoctorMcKay/node-steam-totp).
///
/// Steam uses a modified TOTP algorithm:
///   - HMAC-SHA1 over a 30-second time window (same as RFC 6238)
///   - BUT the output alphabet is "23456789BCDFGHJKMNPQRTVWXY" (26 chars, no vowels/ambiguous chars)
///     instead of 0–9
///   - Code length is 5 characters
///
/// Input: shared_secret — the base64-encoded secret from Steam's mobile authenticator
///        (found in maFile / MobileAuthenticator data).
///
/// Usage:
///   string code = SteamTotp.GenerateAuthCode(sharedSecretBase64);
///   // Pass `code` as the TwoFactorCode when logging in to Steam.
///
/// Also implements Steam confirmation key generation (identity_secret).
/// </summary>
public static class SteamTotp
{
    // Steam's custom alphabet — same as node-steam-totp CHAR_SET
    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";
    private const int CodeLength = 5;
    private const int TimeStep = 30; // seconds

    // ─── Auth code ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generate the current Steam Guard Mobile Authenticator code.
    /// </summary>
    /// <param name="sharedSecretBase64">Base64-encoded shared_secret from your maFile.</param>
    /// <param name="timeOffset">Seconds to add to current UTC time (for clock skew correction).</param>
    public static string GenerateAuthCode(string sharedSecretBase64, int timeOffset = 0)
    {
        byte[] secret = Convert.FromBase64String(sharedSecretBase64);
        long   time   = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + timeOffset;
        return GenerateAuthCodeForTime(secret, time);
    }

    /// <summary>
    /// Generate codes for the current window and ±1 adjacent windows (for clock skew tolerance).
    /// </summary>
    public static string[] GenerateAuthCodesWithSkew(string sharedSecretBase64)
    {
        byte[] secret = Convert.FromBase64String(sharedSecretBase64);
        long   now    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new[]
        {
            GenerateAuthCodeForTime(secret, now - TimeStep),
            GenerateAuthCodeForTime(secret, now),
            GenerateAuthCodeForTime(secret, now + TimeStep),
        };
    }

    private static string GenerateAuthCodeForTime(byte[] secret, long unixTime)
    {
        // Build 8-byte big-endian time counter (floor(time / 30))
        long   counter     = unixTime / TimeStep;
        byte[] counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        // HMAC-SHA1
        using var hmac = new HMACSHA1(secret);
        byte[] hash = hmac.ComputeHash(counterBytes);

        // Dynamic truncation (RFC 4226)
        int offset = hash[^1] & 0x0F;
        int code   = ((hash[offset]     & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   |  (hash[offset + 3] & 0xFF);

        // Map to Steam's custom alphabet
        var sb = new StringBuilder(CodeLength);
        for (int i = 0; i < CodeLength; i++)
        {
            sb.Append(Alphabet[code % Alphabet.Length]);
            code /= Alphabet.Length;
        }
        return sb.ToString();
    }

    // ─── Seconds until next code ──────────────────────────────────────────────

    /// <summary>How many seconds until the current code expires.</summary>
    public static int SecondsUntilChange() =>
        TimeStep - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % TimeStep);

    // ─── Confirmation key (trade/market confirmations) ───────────────────────

    /// <summary>
    /// Generate a confirmation key for trade/market confirmations.
    /// Mirrors node-steam-totp generateConfirmationKey().
    /// </summary>
    /// <param name="identitySecretBase64">Base64-encoded identity_secret from maFile.</param>
    /// <param name="time">Unix timestamp (use DateTimeOffset.UtcNow.ToUnixTimeSeconds()).</param>
    /// <param name="tag">Tag string e.g. "conf", "details", "allow", "cancel".</param>
    public static string GenerateConfirmationKey(
        string identitySecretBase64, long time, string tag)
    {
        byte[] secret = Convert.FromBase64String(identitySecretBase64);

        // Data = big-endian 8-byte time + tag bytes
        byte[] tagBytes = Encoding.UTF8.GetBytes(tag);
        byte[] data     = new byte[8 + tagBytes.Length];
        byte[] timeBytes = BitConverter.GetBytes(time);
        if (BitConverter.IsLittleEndian) Array.Reverse(timeBytes);
        Buffer.BlockCopy(timeBytes, 0, data, 0, 8);
        Buffer.BlockCopy(tagBytes,  0, data, 8, tagBytes.Length);

        using var hmac = new HMACSHA1(secret);
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }

    // ─── Clock offset helper ──────────────────────────────────────────────────

    /// <summary>
    /// Query Steam's time server and return the clock offset in seconds.
    /// Optional — use this if your system clock is significantly wrong.
    /// </summary>
    public static async Task<int> GetSteamTimeOffsetAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.PostAsync(
                "https://api.steampowered.com/ITwoFactorService/QueryTime/v1/", null);
            var json = System.Text.Json.JsonDocument.Parse(
                await resp.Content.ReadAsStringAsync());
            long serverTime = json.RootElement
                .GetProperty("response")
                .GetProperty("server_time")
                .GetInt64();
            return (int)(serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch { return 0; }
    }
}
