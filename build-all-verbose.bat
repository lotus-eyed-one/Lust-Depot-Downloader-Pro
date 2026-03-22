@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul 2>&1

echo.
echo  ============================================================
echo   Lusts Depot Downloader Pro - Build ALL Platforms (Verbose)
echo  ============================================================
echo.

:: ── .NET SDK check ───────────────────────────────────────────────────────────
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found in PATH.
    echo         Install from: https://dotnet.microsoft.com/download
    pause & exit /b 1
)
echo [SDK] dotnet version: 
dotnet --version
echo.

:: ── Set project dir to wherever this bat lives ────────────────────────────────
set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"
set "OUT=%PROJ%\publish"

:: ── EmbeddedConfig generation ─────────────────────────────────────────────────
echo [1/6] Generating EmbeddedConfig from .env...
if exist "%PROJ%\.env" (
    echo       Found .env - running GenerateEmbeddedConfig.ps1
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%PROJ%\GenerateEmbeddedConfig.ps1" "%PROJ%" 2>&1
    if errorlevel 1 (
        echo       pwsh failed, trying legacy powershell.exe...
        powershell -NoProfile -ExecutionPolicy Bypass -File "%PROJ%\GenerateEmbeddedConfig.ps1" "%PROJ%" 2>&1
    )
) else (
    echo       WARNING: .env not found. Generating empty placeholder config.
    echo       Copy .env.template to .env and fill in your keys for full functionality.
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%PROJ%\GenerateEmbeddedConfig.ps1" "%PROJ%" 2>&1
    if errorlevel 1 (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%PROJ%\GenerateEmbeddedConfig.ps1" "%PROJ%" 2>&1
    )
)
echo.

:: ── NuGet restore ─────────────────────────────────────────────────────────────
echo [2/6] Restoring NuGet packages...
dotnet restore "%PROJ%\LustsDepotDownloaderPro.csproj" --verbosity normal 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] NuGet restore failed - see output above for details.
    pause & exit /b 1
)
echo.

:: ── Clean previous builds ─────────────────────────────────────────────────────
echo [3/6] Cleaning previous builds...
if exist "%OUT%" (
    rmdir /s /q "%OUT%" 2>&1
    echo       Removed: %OUT%
)
echo.

:: ── Build targets ─────────────────────────────────────────────────────────────
echo [4/6] Building all platforms (full compiler output shown)...
echo.

set PASS=0
set FAIL=0
set FAILED_TARGETS=

:: ─── win-x64 ──────────────────────────────────────────────────────────────────
echo ┌─────────────────────────────────────────┐
echo   Building: win-x64 (Release)
echo └─────────────────────────────────────────┘
dotnet publish "%PROJ%\LustsDepotDownloaderPro.csproj" ^
    -c Release -r win-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    --verbosity normal ^
    -o "%OUT%\win-x64-release" 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] win-x64
    set /a FAIL+=1
    set "FAILED_TARGETS=!FAILED_TARGETS! win-x64"
) else (
    if exist "%OUT%\win-x64-release\LustsDepotDownloaderPro.exe" (
        for %%A in ("%OUT%\win-x64-release\LustsDepotDownloaderPro.exe") do (
            echo [OK] win-x64  ^|  %%~zA bytes  ^|  %OUT%\win-x64-release\LustsDepotDownloaderPro.exe
        )
        set /a PASS+=1
    ) else (
        echo [FAIL] win-x64 - exe not found after publish
        set /a FAIL+=1
        set "FAILED_TARGETS=!FAILED_TARGETS! win-x64"
    )
)
echo.

:: ─── win-x86 ──────────────────────────────────────────────────────────────────
echo ┌─────────────────────────────────────────┐
echo   Building: win-x86 (Release)
echo └─────────────────────────────────────────┘
dotnet publish "%PROJ%\LustsDepotDownloaderPro.csproj" ^
    -c Release -r win-x86 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    --verbosity normal ^
    -o "%OUT%\win-x86-release" 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] win-x86
    set /a FAIL+=1
    set "FAILED_TARGETS=!FAILED_TARGETS! win-x86"
) else (
    if exist "%OUT%\win-x86-release\LustsDepotDownloaderPro.exe" (
        for %%A in ("%OUT%\win-x86-release\LustsDepotDownloaderPro.exe") do (
            echo [OK] win-x86  ^|  %%~zA bytes  ^|  %OUT%\win-x86-release\LustsDepotDownloaderPro.exe
        )
        set /a PASS+=1
    ) else (
        echo [FAIL] win-x86 - exe not found after publish
        set /a FAIL+=1
        set "FAILED_TARGETS=!FAILED_TARGETS! win-x86"
    )
)
echo.

:: ─── linux-x64 ────────────────────────────────────────────────────────────────
echo ┌─────────────────────────────────────────┐
echo   Building: linux-x64 (Release)
echo └─────────────────────────────────────────┘
dotnet publish "%PROJ%\LustsDepotDownloaderPro.csproj" ^
    -c Release -r linux-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    --verbosity normal ^
    -o "%OUT%\linux-x64-release" 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] linux-x64
    set /a FAIL+=1
    set "FAILED_TARGETS=!FAILED_TARGETS! linux-x64"
) else (
    if exist "%OUT%\linux-x64-release\LustsDepotDownloaderPro" (
        for %%A in ("%OUT%\linux-x64-release\LustsDepotDownloaderPro") do (
            echo [OK] linux-x64  ^|  %%~zA bytes  ^|  %OUT%\linux-x64-release\LustsDepotDownloaderPro
        )
        set /a PASS+=1
    ) else (
        echo [FAIL] linux-x64 - binary not found after publish
        set /a FAIL+=1
        set "FAILED_TARGETS=!FAILED_TARGETS! linux-x64"
    )
)
echo.

:: ─── osx-x64 ──────────────────────────────────────────────────────────────────
echo ┌─────────────────────────────────────────┐
echo   Building: osx-x64 (Release)
echo └─────────────────────────────────────────┘
dotnet publish "%PROJ%\LustsDepotDownloaderPro.csproj" ^
    -c Release -r osx-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    --verbosity normal ^
    -o "%OUT%\osx-x64-release" 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] osx-x64
    set /a FAIL+=1
    set "FAILED_TARGETS=!FAILED_TARGETS! osx-x64"
) else (
    if exist "%OUT%\osx-x64-release\LustsDepotDownloaderPro" (
        for %%A in ("%OUT%\osx-x64-release\LustsDepotDownloaderPro") do (
            echo [OK] osx-x64  ^|  %%~zA bytes  ^|  %OUT%\osx-x64-release\LustsDepotDownloaderPro
        )
        set /a PASS+=1
    ) else (
        echo [FAIL] osx-x64 - binary not found after publish
        set /a FAIL+=1
        set "FAILED_TARGETS=!FAILED_TARGETS! osx-x64"
    )
)
echo.

:: ─── osx-arm64 ────────────────────────────────────────────────────────────────
echo ┌─────────────────────────────────────────┐
echo   Building: osx-arm64 (Release)
echo └─────────────────────────────────────────┘
dotnet publish "%PROJ%\LustsDepotDownloaderPro.csproj" ^
    -c Release -r osx-arm64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    --verbosity normal ^
    -o "%OUT%\osx-arm64-release" 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] osx-arm64
    set /a FAIL+=1
    set "FAILED_TARGETS=!FAILED_TARGETS! osx-arm64"
) else (
    if exist "%OUT%\osx-arm64-release\LustsDepotDownloaderPro" (
        for %%A in ("%OUT%\osx-arm64-release\LustsDepotDownloaderPro") do (
            echo [OK] osx-arm64  ^|  %%~zA bytes  ^|  %OUT%\osx-arm64-release\LustsDepotDownloaderPro
        )
        set /a PASS+=1
    ) else (
        echo [FAIL] osx-arm64 - binary not found after publish
        set /a FAIL+=1
        set "FAILED_TARGETS=!FAILED_TARGETS! osx-arm64"
    )
)
echo.

:: ── PDB cleanup ───────────────────────────────────────────────────────────────
echo [5/6] Removing any stray .pdb files...
for /r "%OUT%" %%F in (*.pdb) do (
    del "%%F" 2>nul
    echo       Deleted: %%F
)
echo.

:: ── Summary ───────────────────────────────────────────────────────────────────
echo [6/6] Build summary
echo  ============================================================
echo   Passed : %PASS% / 5
echo   Failed : %FAIL% / 5
if not "!FAILED_TARGETS!"=="" (
    echo   Failed targets:!FAILED_TARGETS!
    echo.
    echo   To diagnose a single target, run one of the per-platform
    echo   scripts (e.g. build-win-x64-release.bat) or:
    echo.
    echo     dotnet publish -c Release -r win-x64 --self-contained true --verbosity diagnostic
    echo.
    echo   Common causes:
    echo     CS error  - compiler error in source (see output above)
    echo     NETSDK    - missing workload (run: dotnet workload restore)
    echo     MSB3270   - processor architecture mismatch (usually harmless warning)
    echo     NU1xxx    - NuGet package issue (run: dotnet restore)
)
echo  ============================================================
echo.

if %FAIL% gtr 0 (
    pause
    exit /b 1
)
pause
exit /b 0
