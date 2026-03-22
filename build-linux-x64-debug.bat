@echo off
echo ========================================
echo  Linux x64 ^| DEBUG
echo ========================================
echo.
dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: .NET 8 SDK not found. & pause & exit /b 1 )
dotnet restore
dotnet publish -c Debug -r linux-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish\linux-x64-debug
if exist publish\linux-x64-debug\LustsDepotDownloaderPro (
    echo.
    echo [OK] publish\linux-x64-debug\LustsDepotDownloaderPro
    echo [OK] PDB included for debugging
) else ( echo [FAIL] Build failed. & pause & exit /b 1 )
echo.
pause
