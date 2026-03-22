@echo off
echo ========================================
echo  Win x86 ^| RELEASE
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
)
echo.

dotnet restore
dotnet publish -c Release -r win-x86 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    -o publish\win-x86-release
if exist publish\win-x86-release\LustsDepotDownloaderPro.exe (
    echo.
    echo [OK] publish\win-x86-release\LustsDepotDownloaderPro.exe
) else ( echo [FAIL] Build failed. & pause & exit /b 1 )
echo.
pause
