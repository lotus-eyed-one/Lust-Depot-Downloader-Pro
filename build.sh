#!/usr/bin/env bash
set -e

echo "========================================"
echo " Linux x64 | RELEASE"
echo "========================================"
echo

if ! dotnet --version &>/dev/null; then
    echo "ERROR: .NET 8 SDK not found."
    exit 1
fi

echo "[env] Generating EmbeddedConfig from .env..."
if [ -f ".env" ]; then
    bash GenerateEmbeddedConfig.sh "$(pwd)"
else
    echo "[env] WARNING: .env not found. Copy .env.template to .env and fill in your keys."
    bash GenerateEmbeddedConfig.sh "$(pwd)"  # generates empty placeholder
fi
echo

dotnet restore
dotnet publish -c Release -r linux-x64 --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:PublishReadyToRun=true \
    /p:DebugType=none \
    /p:DebugSymbols=false \
    -o publish/linux-x64-release

if [ -f "publish/linux-x64-release/LustsDepotDownloaderPro" ]; then
    echo
    echo "[OK] publish/linux-x64-release/LustsDepotDownloaderPro"
    echo "[OK] Size: $(du -sh publish/linux-x64-release/LustsDepotDownloaderPro | cut -f1)"
else
    echo "[FAIL] Build failed."
    exit 1
fi
