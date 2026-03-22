@echo off
echo ========================================
echo  Linux x64 ^| RELEASE
echo ========================================
echo.
dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: .NET 8 SDK not found. & pause & exit /b 1 )

echo [env] Generating EmbeddedConfig from .env...
if exist .env (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0."
    if errorlevel 1 powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0."
)
echo.

dotnet restore
dotnet publish -c Release -r linux-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    -o publish\linux-x64-release
echo.
pause
