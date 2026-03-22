// NOTE: This file is kept for API compatibility only.
// The actual CDN downloading is now done via SteamKit2.CDN.Client inside SteamSession,
// which handles authentication, decryption, and decompression correctly.
// The old raw-HTTP approach in this file produced 401/403 errors and missing decryption.

namespace LustsDepotDownloaderPro.Utils;

[Obsolete("Use SteamSession.CdnClient (SteamKit2.CDN.Client) instead.")]
public class CdnClient
{
    // Kept empty — no longer used by workers or session builder.
    // Remove entirely once all call-sites are verified clean.
}
