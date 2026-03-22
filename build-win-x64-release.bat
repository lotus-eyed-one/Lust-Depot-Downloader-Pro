@echo off
echo ========================================
echo  Win x64 ^| RELEASE
echo ========================================
echo.
dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: .NET 8 SDK not found. & pause & exit /b 1 )

echo [env] Generating EmbeddedConfig from .env...
if exist .env (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0."
    if errorlevel 1 powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0."
) else (
    echo [env] WARNING: .env not found. Copy .env.template to .env and fill in your keys.
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0." 2>nul
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0." 2>nul
)
echo.

dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    -o publish\win-x64-release
if exist publish\win-x64-release\LustsDepotDownloaderPro.exe (
    echo.
    echo [OK] publish\win-x64-release\LustsDepotDownloaderPro.exe
    for %%A in (publish\win-x64-release\LustsDepotDownloaderPro.exe) do echo [OK] Size: %%~zA bytes
) else ( echo [FAIL] Build failed. & pause & exit /b 1 )
echo.
pause
