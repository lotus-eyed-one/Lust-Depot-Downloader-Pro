#!/usr/bin/env python3
"""
parse_luckygob.py  —  called by LustsDepotDownloaderPro's ManifestSourceFetcher
                       to decode the Go-gob AppInfo blob from luckygametools/steam-cfg.

Usage:
    python parse_luckygob.py <input_blob_file> <output_json_file>

Input:  raw bytes of the decrypted (AES+XOR) gob blob
Output: JSON  {"depots": [{"id":N,"manifestId":N,"decryptKey":"hex","manifestData":"b64"}, ...]}

Requires:  pygob  (already bundled in Scripts/pygob/)
"""

import sys, os, json, base64, collections

# Make the bundled pygob importable
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pygob

AppInfo = collections.namedtuple(
    'AppInfo',
    ['Appid', 'Licenses', 'App', 'Depots', 'EncryptedAppTicket', 'AppOwnershipTicket']
)

def parse(blob_path: str, out_path: str):
    with open(blob_path, 'rb') as f:
        data = f.read()

    content_gob = pygob.load_all(bytes(data))
    app_info = AppInfo._make(*content_gob)

    result = {"depots": []}
    for depot in app_info.Depots:
        entry = {
            "id":         int(depot.Id),
            "manifestId": int(depot.Manifests.Id),
            "decryptKey": depot.Decryptkey.hex() if depot.Decryptkey else "",
            "manifestData": base64.b64encode(depot.Manifests.Data).decode()
                            if depot.Manifests.Data else "",
        }
        result["depots"].append(entry)

    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(result, f)

if __name__ == '__main__':
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <blob_file> <output_json>", file=sys.stderr)
        sys.exit(1)
    try:
        parse(sys.argv[1], sys.argv[2])
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)
