#!/usr/bin/env python3
"""
Manifest and Key Generator for Lusts Depot Downloader Pro
Integrates with Steam depot libraries to generate manifest and key files
Based on DepotDownloaderMod's steam_storage.py
"""

import json
import sys
import os
import requests
from pathlib import Path
from typing import Dict, List, Optional, Tuple

class ManifestKeyGenerator:
    """Generates manifest and depot key files for Steam apps"""
    
    DEPOT_LIBRARIES = [
        "https://raw.githubusercontent.com/SteamDatabase/SteamTracking/master/Random/DepotDownloadConfig.json",
        "https://bbs.steamtools.net/api/depot_keys.json"
    ]
    
    def __init__(self, output_dir: str = "manifests"):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(exist_ok=True, parents=True)
        self.depot_cache: Dict[str, Dict] = {}
        
    def fetch_depot_data(self) -> Dict:
        """Fetch depot data from various sources"""
        print("📥 Fetching depot data from libraries...")
        
        all_data = {
            "depots": {},
            "manifests": {}
        }
        
        for url in self.DEPOT_LIBRARIES:
            try:
                print(f"  ↳ Fetching from {url}")
                response = requests.get(url, timeout=30)
                if response.status_code == 200:
                    data = response.json()
                    # Merge data
                    if "depots" in data:
                        all_data["depots"].update(data["depots"])
                    if "manifests" in data:
                        all_data["manifests"].update(data["manifests"])
                    print(f"    ✓ Successfully fetched data")
            except Exception as e:
                print(f"    ✗ Failed: {e}")
                continue
        
        self.depot_cache = all_data
        print(f"✓ Loaded {len(all_data.get('depots', {}))} depots")
        return all_data
    
    def get_depot_keys(self, app_id: int, depot_id: Optional[int] = None) -> Dict[int, str]:
        """Get depot keys for an app"""
        if not self.depot_cache:
            self.fetch_depot_data()
        
        keys = {}
        depots = self.depot_cache.get("depots", {})
        
        app_id_str = str(app_id)
        if app_id_str in depots:
            app_depots = depots[app_id_str]
            
            if depot_id:
                # Get specific depot key
                depot_id_str = str(depot_id)
                if depot_id_str in app_depots:
                    keys[depot_id] = app_depots[depot_id_str].get("key", "")
            else:
                # Get all depot keys for app
                for dep_id, dep_data in app_depots.items():
                    if "key" in dep_data:
                        keys[int(dep_id)] = dep_data["key"]
        
        return keys
    
    def get_manifest_id(self, app_id: int, depot_id: int, branch: str = "public") -> Optional[int]:
        """Get manifest ID for a depot"""
        if not self.depot_cache:
            self.fetch_depot_data()
        
        manifests = self.depot_cache.get("manifests", {})
        
        app_id_str = str(app_id)
        if app_id_str in manifests:
            app_manifests = manifests[app_id_str]
            depot_id_str = str(depot_id)
            
            if depot_id_str in app_manifests:
                depot_manifests = app_manifests[depot_id_str]
                
                if branch in depot_manifests:
                    return int(depot_manifests[branch])
        
        return None
    
    def generate_depot_keys_file(self, app_id: int, output_file: Optional[str] = None) -> str:
        """Generate depot keys file in format: depotID;hexKey"""
        print(f"\n📝 Generating depot keys file for AppID {app_id}...")
        
        keys = self.get_depot_keys(app_id)
        
        if not keys:
            print(f"  ✗ No depot keys found for AppID {app_id}")
            return ""
        
        if not output_file:
            output_file = self.output_dir / f"depot_keys_{app_id}.txt"
        
        with open(output_file, 'w') as f:
            f.write(f"# Depot Keys for AppID {app_id}\n")
            f.write(f"# Format: depotID;hexKey\n\n")
            
            for depot_id, key in keys.items():
                f.write(f"{depot_id};{key}\n")
        
        print(f"  ✓ Saved {len(keys)} depot keys to: {output_file}")
        return str(output_file)
    
    def generate_download_batch(self, app_id: int, branch: str = "public", 
                                output_dir: Optional[str] = None) -> str:
        """Generate batch file for downloading with DepotDownloader"""
        print(f"\n📦 Generating download batch for AppID {app_id}...")
        
        keys = self.get_depot_keys(app_id)
        
        if not keys:
            print(f"  ✗ No depot information found for AppID {app_id}")
            return ""
        
        if not output_dir:
            output_dir = f"downloads/{app_id}"
        
        # Generate depot keys file first
        keys_file = self.generate_depot_keys_file(app_id)
        
        # Create batch file
        batch_file = self.output_dir / f"download_{app_id}.bat"
        
        with open(batch_file, 'w') as f:
            f.write("@echo off\n")
            f.write(f"REM Download batch for AppID {app_id}\n")
            f.write(f"REM Generated by Lusts Depot Downloader Pro\n\n")
            
            for depot_id in keys.keys():
                manifest_id = self.get_manifest_id(app_id, depot_id, branch)
                
                f.write(f"REM Depot {depot_id}\n")
                
                cmd = f'LustsDepotDownloaderPro.exe --app {app_id} --depot {depot_id}'
                
                if manifest_id:
                    cmd += f' --manifest {manifest_id}'
                
                cmd += f' --depot-keys "{keys_file}"'
                cmd += f' --branch {branch}'
                cmd += f' --output "{output_dir}"'
                cmd += ' --terminal-ui\n\n'
                
                f.write(cmd)
        
        print(f"  ✓ Batch file created: {batch_file}")
        return str(batch_file)
    
    def generate_manifest_list(self, app_id: int) -> Dict:
        """Generate list of available manifests for an app"""
        print(f"\n📋 Generating manifest list for AppID {app_id}...")
        
        if not self.depot_cache:
            self.fetch_depot_data()
        
        manifests = self.depot_cache.get("manifests", {})
        app_manifests = manifests.get(str(app_id), {})
        
        result = {
            "app_id": app_id,
            "depots": {}
        }
        
        for depot_id, branches in app_manifests.items():
            result["depots"][depot_id] = branches
        
        # Save to JSON
        output_file = self.output_dir / f"manifests_{app_id}.json"
        with open(output_file, 'w') as f:
            json.dump(result, f, indent=2)
        
        print(f"  ✓ Manifest list saved: {output_file}")
        print(f"  ✓ Found {len(result['depots'])} depots")
        
        return result

def main():
    """Main CLI interface"""
    import argparse
    
    parser = argparse.ArgumentParser(
        description="Manifest and Key Generator for Lusts Depot Downloader Pro"
    )
    parser.add_argument("app_id", type=int, help="Steam AppID")
    parser.add_argument("--output", "-o", help="Output directory", default="manifests")
    parser.add_argument("--branch", "-b", help="Branch name", default="public")
    parser.add_argument("--keys-only", action="store_true", help="Generate only depot keys file")
    parser.add_argument("--batch", action="store_true", help="Generate download batch file")
    parser.add_argument("--list-manifests", action="store_true", help="List available manifests")
    
    args = parser.parse_args()
    
    generator = ManifestKeyGenerator(output_dir=args.output)
    
    # Fetch data
    generator.fetch_depot_data()
    
    if args.keys_only:
        generator.generate_depot_keys_file(args.app_id)
    elif args.list_manifests:
        generator.generate_manifest_list(args.app_id)
    elif args.batch:
        generator.generate_download_batch(args.app_id, args.branch)
    else:
        # Generate everything
        generator.generate_depot_keys_file(args.app_id)
        generator.generate_manifest_list(args.app_id)
        generator.generate_download_batch(args.app_id, args.branch)
    
    print("\n✅ Generation complete!")

if __name__ == "__main__":
    main()
