# Python Scripts for Lusts Depot Downloader Pro

## Overview

This directory contains Python helper scripts for generating manifest and depot key files.

## Prerequisites

- **Python 3.8+**
- **pip** (Python package manager)

## Installation

```bash
# Install dependencies
pip install -r requirements.txt
```

Or manually:
```bash
pip install requests
```

---

## Scripts

### 📄 generate_manifests.py

**Purpose**: Generate depot keys and manifest IDs from online Steam libraries.

**Features**:
- Fetch depot keys from multiple sources
- Generate manifest ID lists
- Create download batch files
- Support for custom output directories

**Usage**:

```bash
# Basic usage - generates everything
python generate_manifests.py <appid>

# Generate only depot keys
python generate_manifests.py <appid> --keys-only

# Generate download batch file
python generate_manifests.py <appid> --batch

# List available manifests
python generate_manifests.py <appid> --list-manifests

# Custom output directory
python generate_manifests.py <appid> --output my_data

# Specify branch
python generate_manifests.py <appid> --branch beta
```

**Examples**:

```bash
# Counter-Strike: Global Offensive (730)
python generate_manifests.py 730

# GTA V (271590) with custom output
python generate_manifests.py 271590 --output gtav_data

# Download batch for beta branch
python generate_manifests.py 730 --branch beta --batch
```

**Generated Files**:

1. **depot_keys_<appid>.txt**
   - Format: `depotID;hexKey`
   - Use with `--depot-keys` flag

2. **manifests_<appid>.json**
   - JSON list of available manifests
   - Organized by depot and branch

3. **download_<appid>.bat** (if --batch used)
   - Windows batch file
   - Ready-to-run download commands
   - Uses generated depot keys

**Output Example**:

```
manifests/
├── depot_keys_730.txt
├── manifests_730.json
└── download_730.bat
```

---

## Data Sources

The script fetches from:
1. **SteamDatabase** - Community-maintained depot configs
2. **SteamTools BBS** - Chinese depot key library
3. *(More sources can be added)*

---

## Troubleshooting

### "Module not found: requests"
```bash
pip install requests
```

### "Connection timeout"
- Check internet connection
- Some sources may be temporarily unavailable
- Script will continue with available sources

### "No depot keys found for AppID"
- App may not be in the libraries yet
- Try fetching manually from:
  - https://bbs.steamtools.net
  - https://github.com/SteamDatabase

---

## Integration with Main Program

### Workflow:

```bash
# Step 1: Generate depot keys
cd Scripts
python generate_manifests.py 730 --keys-only

# Step 2: Use with downloader
cd ..
./LustsDepotDownloaderPro --app 730 --depot-keys Scripts/manifests/depot_keys_730.txt --output ./games/csgo
```

### Automated Download:

```bash
# Generate batch file
python generate_manifests.py 730 --batch

# Run the batch
cd manifests
./download_730.bat  # Windows
# or
bash download_730.sh  # Linux/Mac (if you modify it)
```

---

## Advanced Usage

### Custom Depot Sources

Edit `generate_manifests.py` and add to `DEPOT_LIBRARIES`:

```python
DEPOT_LIBRARIES = [
    "https://your-custom-source.com/depot_keys.json",
    # ... existing sources
]
```

### Programmatic Usage

```python
from generate_manifests import ManifestKeyGenerator

generator = ManifestKeyGenerator(output_dir="my_output")
generator.fetch_depot_data()

# Get depot keys
keys = generator.get_depot_keys(app_id=730)
print(keys)  # {731: 'hexkey1', 732: 'hexkey2', ...}

# Get manifest ID
manifest_id = generator.get_manifest_id(
    app_id=730,
    depot_id=731,
    branch="public"
)
print(manifest_id)  # 7617088375292372759

# Generate files
generator.generate_depot_keys_file(730)
generator.generate_manifest_list(730)
generator.generate_download_batch(730, branch="public")
```

---

## File Formats

### depot_keys_<appid>.txt
```
# Depot Keys for AppID 730
# Format: depotID;hexKey

731;E5A1D6C2F8B3A4E9D7C1F2A8B4C6E3D9
732;A4B9E3F7C2D8A1E6B3F9C4D7E2A8B1C6
733;C6E2A9D3F8B1C4E7A2D9F3B8C1E6A4D7
```

### manifests_<appid>.json
```json
{
  "app_id": 730,
  "depots": {
    "731": {
      "public": "7617088375292372759",
      "beta": "1234567890123456789"
    },
    "732": {
      "public": "9876543210987654321"
    }
  }
}
```

### download_<appid>.bat
```batch
@echo off
REM Download batch for AppID 730

REM Depot 731
LustsDepotDownloaderPro.exe --app 730 --depot 731 --manifest 7617088375292372759 --depot-keys "depot_keys_730.txt" --branch public --output "downloads/730" --terminal-ui

REM Depot 732
LustsDepotDownloaderPro.exe --app 730 --depot 732 --depot-keys "depot_keys_730.txt" --branch public --output "downloads/730" --terminal-ui
```

---

## Tips

1. **Keep Manifests Updated**: Re-run script periodically to get latest manifest IDs
2. **Store Keys Securely**: depot_keys files contain sensitive information
3. **Batch Downloads**: Use batch files for automated downloading of all depots
4. **Custom Branches**: Specify `--branch beta` for beta/experimental versions

---

## Support

For issues with the Python scripts:
1. Check Python version: `python --version` (need 3.8+)
2. Verify requests installed: `pip list | grep requests`
3. Check internet connection
4. Enable debug output in script if needed

---

**Script Version**: 1.0.0  
**Compatible with**: Lusts Depot Downloader Pro v1.0.0+  
**Last Updated**: March 17, 2026
