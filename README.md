<div align="center">

# 🔥 Lusts Depot Downloader Pro

**A Steam depot downloader — but with extra fun.**

[![License: GPL-2.0](https://img.shields.io/badge/License-GPL%202.0-blue.svg)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey.svg)]()
[![Version](https://img.shields.io/badge/Version-1.0.0-green.svg)]()
[![SteamKit2](https://img.shields.io/badge/SteamKit2-3.2.0-1b2838.svg)]()

*Download Steam depots fast, resumably, and without the headache.*

</div>

---

## 📌 What Is This?

**Lusts Depot Downloader Pro** is a command-line Steam depot downloader built on top of [SteamKit2](https://github.com/SteamRE/SteamKit). It grabs game depots directly from Steam's CDN — anonymously or with an account — with multi-threaded downloading, **pause/resume support**, CDN failover, and a clean terminal UI.

It's a supercharged depot downloader with **community manifest support**, **checkpoint resuming**, and quality-of-life features. Perfect for backing up games you own, downloading older game versions, or getting free-to-play content.

> ⚠️ **For educational purposes and backup of owned content only. Always comply with Steam's Terms of Service.**

---

## ✨ Features

### Core Features ✅
| Feature | Details |
|---|---|
| 🚀 **Multi-threaded** | 1–64 concurrent download workers |
| ⏸️ **Pause & Resume** | **FULLY WORKING** — Checkpoint system, pick up exactly where you left off |
| 🌐 **CDN Failover** | Automatic fallback across 20+ Steam CDN servers |
| 📄 **Community Manifests** | Pulls from ManifestHub — download games without owning them |
| 🔑 **Depot Keys** | Load keys from file for encrypted depots |
| 🌿 **Branch Support** | Public, beta, or any custom branch |
| 🔍 **File Filtering** | Wildcard and regex filters — download only what you need |
| ✅ **Checksum Validation** | Verify file integrity after download |
| 🎨 **Terminal UI** | Live progress bars, speed, ETA — powered by Spectre.Console |
| 📦 **Single Executable** | Self-contained, no installs, no dependencies |
| 🖥️ **Cross-Platform** | Windows, Linux, macOS |

### Authentication ✅
| Feature | Details |
|---|---|
| 🔓 **Anonymous Login** | Download free-to-play & public content without account |
| 🔐 **Username/Password** | Standard Steam authentication |
| 🛡️ **Steam Guard** | Email codes prompted automatically |
| 📱 **2FA Support** | Mobile authenticator codes prompted when needed |
| 💾 **Save Credentials** | `--remember-password` to avoid retyping |

### Advanced Features ✅
| Feature | Details |
|---|---|
| 🎯 **Platform Filtering** | OS and architecture-specific depot selection |
| 🌍 **Language Selection** | Download only your language's depot |
| 📊 **Real-time Stats** | Live MB/s speed, percentage, and ETA |
| 🔄 **Auto Retry** | Exponential backoff on chunk failures |
| 🐍 **Python Scripts** | Manifest and key generation from community sources |
| 🔧 **GUI-Ready CLI** | Parse-friendly output for wrapping in GUI apps |
| 🎛️ **All Platforms/Languages** | Download every platform/language with one flag |

### ⚠️ Not Yet Implemented
| Feature | Status |
|---|---|
| 🎯 **Workshop Downloads** | CLI options exist but not functional — needs Web API implementation |
| 📱 **QR Code Auth** | Not available in SteamKit2 3.2.0 — use username/password instead |

---

## 🛠️ Installation

### Option 1 — Pre-built Executable *(Recommended)*
1. Download the latest release
2. Extract `LustsDepotDownloaderPro.exe` (Windows) or `LustsDepotDownloaderPro` (Linux/macOS)
3. Run from your terminal

### Option 2 — Build from Source

**Prerequisites:**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.8+ *(optional, for manifest scripts)*

**Windows:**
```batch
cd LustsDepotDownloaderPro
build-win-x64-release.bat
# Output: publish\win-x64\LustsDepotDownloaderPro.exe
```

**Build All Platforms:**
```batch
build.bat
# Builds: win-x64, win-x86, linux-x64, osx-x64, osx-arm64
```

**Linux / macOS:**
```bash
cd LustsDepotDownloaderPro
chmod +x build.sh
./build.sh
# Output: publish/linux-x64/LustsDepotDownloaderPro
```

---

## 📖 Usage

### The Basics

```bash
# Download a game anonymously (works for free-to-play & public depots)
LustsDepotDownloaderPro --app 730 --output "C:\Games\CSGO"

# Download with your Steam account (required for paid games you own)
LustsDepotDownloaderPro --app 730 --username myuser --password mypass --output "C:\Games\CSGO"

# Download a specific depot + manifest
LustsDepotDownloaderPro --app 730 --depot 731 --manifest 7617088375292372759 \
  --depot-keys depot_keys.txt --output "C:\Games\CSGO"
```

### ⏸️ Pause & Resume (FULLY WORKING!)

Press **Ctrl+C once** to pause gracefully. The download stops cleanly and saves a checkpoint automatically.

**What you'll see:**
```
⏸  Pausing download...
Checkpoint will be saved. Press Ctrl+C again to force quit.

[Download stops]

⏸  Download paused successfully!
Checkpoint: D:\Games\CSGO\730_Counter-Strike Global Offensive\checkpoint_730.json

To resume, run:
  --app 730 --output "D:\Games\CSGO" --resume "D:\Games\CSGO\730_Counter-Strike Global Offensive\checkpoint_730.json"
```

**Resume exactly where you left off:**
```bash
# Copy and paste the command shown above, or manually specify:
LustsDepotDownloaderPro --app 730 --output "C:\Games\CSGO" \
  --resume "C:\Games\CSGO\730_Counter-Strike Global Offensive\checkpoint_730.json"
```

> 💡 **Checkpoint location:** `<output>/<appid>_<AppName>/checkpoint_<appid>.json`  
> 💡 **Press Ctrl+C twice** to force quit immediately (checkpoint may not save)

**How it works:**
- ✅ Checkpoint saves every 100 chunks during download
- ✅ Checkpoint saves when you press Ctrl+C once
- ✅ Resume skips already-downloaded chunks
- ✅ No re-downloading — picks up exactly where it stopped
- ✅ Works across restarts, network issues, or user pauses

### 🚀 High-Speed Downloading

Workers are parallel download threads. More workers = faster downloads (up to your connection's limit).

```bash
# Sweet spot for most connections (recommended)
LustsDepotDownloaderPro --app 730 --output "C:\Games\CSGO" --max-downloads 32

# Maximum speed (fast connections only — may trigger CDN rate limits)
LustsDepotDownloaderPro --app 730 --output "C:\Games\CSGO" --max-downloads 64
```

| Workers | Best For | Expected Speed |
|---------|----------|----------------|
| `1-4` | Slow/metered connections | ~1–2 MB/s |
| `8` *(default)* | Safe baseline | ~3–5 MB/s |
| `16` | Good home broadband | ~5–10 MB/s |
| `32` ✅ | **Sweet spot — recommended** | ~8–20 MB/s |
| `64` | Very fast fiber (may hit CDN limits) | ~15–30+ MB/s |

> 💡 **Above 32 workers**, Steam's CDN may rate-limit you. You'll see "operation was canceled" warnings. Drop to `--max-downloads 32` for better results.

### 🔍 File Filtering

Create a `filelist.txt`:
```
# Wildcards
*.dll
*.exe
maps/*.bsp

# Regex (prefix with regex:)
regex:^models/.*\.(mdl|vtx|vvd)$

# Exact paths
game/csgo.exe
game/engine.dll
```

```bash
LustsDepotDownloaderPro --app 730 --filelist filelist.txt --output "C:\Games\CSGO"
```

### 🔐 Authentication Examples

```bash
# Save credentials (encrypted locally)
LustsDepotDownloaderPro --app 730 --username myuser --password mypass \
  --remember-password --output "C:\Games"

# Steam Guard code will be prompted automatically
LustsDepotDownloaderPro --app 730 --username myuser --password mypass --output "C:\Games"
# Enter Steam Guard code: ______

# 2FA mobile authenticator code will be prompted
LustsDepotDownloaderPro --app 730 --username myuser --password mypass --output "C:\Games"
# Enter 2FA code: ______
```

> ⚠️ **QR Code authentication (`--qr`) is NOT available** in this version (SteamKit2 3.2.0 limitation). Use standard username/password authentication instead.

### 🌿 Branch Support

```bash
# Download from beta branch
LustsDepotDownloaderPro --app 730 --branch beta --output "C:\Games\CSGO-Beta"

# Password-protected branch
LustsDepotDownloaderPro --app 730 --branch staging --branch-password secretword --output "C:\Games"
```

### 🌍 Platform & Language Filtering

```bash
# Windows 64-bit, English only
LustsDepotDownloaderPro --app 730 --os windows --os-arch 64 --language english --output "C:\Games"

# Download all platforms and all languages
LustsDepotDownloaderPro --app 730 --all-platforms --all-languages --output "C:\Games"
```

### ✅ Other Useful Options

```bash
# Validate checksums after download
LustsDepotDownloaderPro --app 730 --output "C:\Games\CSGO" --validate

# Enable debug logging
LustsDepotDownloaderPro --app 730 --output "C:\Games\CSGO" --debug

# Override CDN cell (if having connection issues)
LustsDepotDownloaderPro --app 730 --cellid 0 --output "C:\Games"
```

---

## 🎛️ All Command Line Options

### Essential

| Option | Short | Description | Example |
|--------|-------|-------------|---------|
| `--app` | `-a` | AppID to download | `--app 730` |
| `--depot` | `-d` | Specific DepotID (downloads all if omitted) | `--depot 731` |
| `--manifest` | `-m` | Specific Manifest ID | `--manifest 7617088375292372759` |
| `--output` | `-o` | Output directory | `--output "C:\Games"` |

### Authentication

| Option | Short | Description | Example |
|--------|-------|-------------|---------|
| `--username` | `-u` | Steam username | `--username myuser` |
| `--password` | `-p` | Steam password | `--password mypass` |
| `--remember-password` | `-rp` | Save credentials for next time | `--remember-password` |
| ~~`--qr`~~ | | ⚠️ **Not available** (SteamKit2 3.2.0 limitation) | — |

### Depot & Manifest

| Option | Short | Description | Example |
|--------|-------|-------------|---------|
| `--depot-keys` | `-dk` | Depot keys file | `--depot-keys keys.txt` |
| `--manifest-file` | `-mf` | Local manifest file | `--manifest-file game.manifest` |
| `--app-token` | `-at` | App access token | `--app-token 1234567890` |
| `--branch` | `-b` | Branch name (default: public) | `--branch beta` |
| `--branch-password` | `-bp` | Branch password | `--branch-password secret` |

### Download Control

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--max-downloads` | `-md` | Concurrent workers (1–64) | `8` |
| `--resume` | `-r` | Resume from checkpoint file | — |
| `--validate` | `-v` | Verify checksums after download | `false` |
| `--pause` | | Pause active download (N/A in current design) | — |
| `--status` | `-s` | Show current download status | — |

### Filtering

| Option | Short | Description | Example |
|--------|-------|-------------|---------|
| `--filelist` | `-fl` | File filter list | `--filelist filters.txt` |
| `--os` | | Target OS (`windows`/`macos`/`linux`) | `--os windows` |
| `--os-arch` | `-arch` | Architecture (`32`/`64`) | `--os-arch 64` |
| `--language` | `-lang` | Language | `--language english` |
| `--all-platforms` | `-ap` | Download all platform depots | `--all-platforms` |
| `--all-languages` | `-al` | Download all language depots | `--all-languages` |

### Workshop ⚠️

| Option | Short | Description | Status |
|--------|-------|-------------|--------|
| `--pubfile` | `-pf` | PublishedFileId | ⚠️ Not implemented |
| `--ugc` | | UGC ID | ⚠️ Not implemented |

> Workshop downloads show a warning message and fall back to standard app download. Full implementation requires Web API integration (planned for future release).

### Misc

| Option | Short | Description |
|--------|-------|-------------|
| `--debug` | | Verbose debug logging |
| `--terminal-ui` | `-tui` | Enable live terminal UI (default: on) |
| `--cellid` | `-c` | Override CDN cell ID |
| `--loginid` | `-lid` | Steam Login ID (for running multiple instances) |
| `--api-key` | `-key` | GitHub API key for community manifest sources |

---

## 🐍 Python Scripts

Generate depot keys and manifests from community sources before downloading.

```bash
cd Scripts
pip install -r requirements.txt

# Generate everything for an app
python generate_manifests.py 730

# Keys only
python generate_manifests.py 730 --keys-only

# Generate a ready-to-run batch file
python generate_manifests.py 730 --batch

# List available manifests
python generate_manifests.py 730 --list-manifests

# Custom output folder
python generate_manifests.py 730 --output my_manifests
```

**Output files:**
- `depot_keys_<appid>.txt` — depot keys in `depotID;hexKey` format
- `manifests_<appid>.json` — available manifest IDs
- `download_<appid>.bat` — ready-to-run download batch file

---

## 📋 File Formats

### Depot Keys (`depot_keys.txt`)
```
# Format: depotID;hexKey
# Lines starting with # are ignored

731;E5A1D6C2F8B3A4E9D7C1F2A8B4C6E3D9
732;A4B9E3F7C2D8A1E6B3F9C4D7E2A8B1C6
733;C6E2A9D3F8B1C4E7A2D9F3B8C1E6A4D7
```

### File Filter List (`filelist.txt`)
```
# Wildcards
*.dll
*.exe
maps/*.bsp
bin/*

# Regex (prefix with regex:)
regex:^models/.*\.(mdl|vtx|vvd)$

# Exact file paths
game/csgo.exe
game/engine.dll
```

### Checkpoint File *(auto-generated, don't edit)*
```json
{
  "CompletedChunks": ["abc123...", "def456..."],
  "LastSaved": "2026-03-19T18:51:49Z"
}
```

---

## 📁 Project Structure

```
LustsDepotDownloaderPro/
├── Core/
│   ├── DownloadEngine.cs          # Main orchestration & worker management
│   ├── DownloadWorker.cs          # Per-worker chunk download logic
│   ├── ChunkScheduler.cs          # Thread-safe chunk queue
│   ├── GlobalProgress.cs          # Shared progress tracking
│   ├── Checkpoint.cs              # Pause/resume state persistence
│   ├── FileAssembler.cs           # High-performance cached file writing
│   └── DownloadSessionBuilder.cs  # Session prep & manifest loading
├── Steam/
│   ├── SteamSession.cs            # Auth, CDN tokens, connection
│   ├── ManifestParser.cs          # Binary manifest decoding
│   └── ManifestSourceFetcher.cs   # Community manifest sources
├── Models/
│   ├── DownloadOptions.cs         # CLI option model
│   └── DownloadSession.cs         # Session state & checkpoint model
├── Utils/
│   ├── Logger.cs                  # Structured logging
│   ├── FileUtils.cs               # File path helpers & credentials
│   ├── CdnClient.cs               # CDN client wrapper
│   └── VZipDecompressor.cs        # Steam VZip decompression
├── UI/
│   └── TerminalUI.cs              # Spectre.Console live UI
├── Scripts/
│   └── generate_manifests.py      # Python manifest/key generator
├── Program.cs                     # Entry point & CLI parsing
├── LustsDepotDownloaderPro.csproj
├── build.bat / build.sh
└── README.md
```

---

## 🔧 GUI Integration

The tool is designed to be wrapped by GUI apps. Parse its stdout for progress updates.

```csharp
var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "LustsDepotDownloaderPro.exe",
        Arguments = $"--app {appId} --output \"{outputDir}\" --max-downloads 16",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }
};

process.OutputDataReceived += (s, e) =>
{
    // Log format: [HH:mm:ss.fff] [LEVEL] Message
    UpdateProgressBar(e.Data);
};

process.Start();
process.BeginOutputReadLine();
await process.WaitForExitAsync();
```

**Exit codes:**
| Code | Meaning |
|------|---------|
| `0` | ✅ Success — download completed |
| `1` | ❌ Error — check logs |
| `2` | ⏸️ Paused — checkpoint saved |

---

## 🚨 Troubleshooting

### ❌ "Failed to connect to Steam"
- Check your internet connection
- Try a different CDN cell: `--cellid 0`
- Check if firewall/antivirus is blocking outbound connections
- Try again later (Steam servers may be down)

### ❌ "Failed to get depot key" / "AccessDenied"
- Use `--depot-keys` with a keys file
- Some depots require account ownership — login with `--username` and `--password`
- Generate keys using Python script: `python generate_manifests.py <appid> --keys-only`

### ❌ "Manifest not found"
- Double-check your AppID and DepotID
- Try `--branch beta` or another branch
- Community sources may not have this depot yet
- Use: `python generate_manifests.py <appid> --list-manifests`

### ❌ "All CDN servers failed" / 401 Unauthorized
- Reduce workers: `--max-downloads 16`
- **For paid games:** Login with `--username` and `--password`
- Check if the game is region-locked
- Your ISP may be throttling Steam traffic

### ⚠️ Many "operation was canceled" warnings
- **Too many workers overwhelming CDN**
- Drop to `--max-downloads 32` (you'll often get same or better speed)
- Above 32 workers triggers CDN rate limiting

### 🐢 Download is slow
- **Default is only 8 workers** — increase to `--max-downloads 32`
- Check your internet speed (the bottleneck may be your connection)
- Try different CDN cell: `--cellid 0`

### ⚠️ "file is being used by another process" warnings
- **This is harmless** — multiple workers briefly contend on the same large file
- Worker retries automatically and file ends up correct
- Download continues normally

### ⏸️ Pause/Resume not working
- **Always include `--app` and `--output` when resuming**
- Example: `--app 730 --output "C:\Games" --resume "checkpoint_730.json"`
- Checkpoint only stores chunk progress, not the original command

### ⚠️ "QR code authentication not supported"
- **QR auth is not available in this version** (SteamKit2 3.2.0 limitation)
- **Workaround:** Use standard `--username` and `--password`
- Steam Guard/2FA codes will be prompted when needed

---

## 📝 Known Limitations

| Feature | Status | Workaround |
|---------|--------|------------|
| QR Code Auth | ❌ Not available (SteamKit2 3.2.0) | Use `--username` & `--password` |
| Workshop Downloads | ❌ Not implemented | Needs Web API integration |
| Bandwidth Limiting | ❌ Not implemented | Use OS-level tools |
| Download Scheduling | ❌ Not implemented | Use task scheduler |

---

## 🗺️ Roadmap

- [ ] Implement Workshop downloads (Web API integration)
- [ ] Upgrade to SteamKit2 3.4+ (QR code support)
- [ ] Bandwidth / speed limiting
- [ ] Delta patching for incremental updates
- [ ] Web UI
- [ ] Docker container
- [ ] Auto-update mechanism
- [ ] Download scheduling & queuing

---

## 🙏 Credits

<div align="center">

*Made with ❤️ by The Lust — Version 1.0.0 — March 2026*

*Built on [SteamKit2](https://github.com/SteamRE/SteamKit)*

*Big thanks to [oureveryday](https://github.com/oureveryday) for the foundational depot downloader work*

</div>

---

## ⚖️ Legal

**For educational purposes and backup of content you own only.**

This tool downloads content directly from Steam's CDN. Always comply with:
- Steam Subscriber Agreement
- Steam Terms of Service  
- Local laws regarding software licensing

**I am not responsible for misuse of this software.**

*Do note that abuse of this software, or owning it without my proper permission, is not allowed, nor selling or any kind of profit made related the use of it, can result in serious consequences!*
