@echo off
echo ========================================
echo  macOS x64 ^| RELEASE
echo ========================================
echo.
dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: .NET 8 SDK not found. & pause & exit /b 1 )

if exist .env (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0."
    if errorlevel 1 powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateEmbeddedConfig.ps1" "%~dp0."
)

dotnet restore
dotnet publish -c Release -r osx-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    -o publish\osx-x64-release
echo.
pause
