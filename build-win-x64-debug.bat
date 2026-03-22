@echo off
echo ========================================
echo  Win x64 ^| DEBUG
echo ========================================
echo.
dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: .NET 8 SDK not found. & pause & exit /b 1 )
dotnet restore
dotnet publish -c Debug -r win-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish\win-x64-debug
if exist publish\win-x64-debug\LustsDepotDownloaderPro.exe (
    echo.
    echo [OK] publish\win-x64-debug\LustsDepotDownloaderPro.exe
    echo [OK] PDB included for debugging
) else ( echo [FAIL] Build failed. & pause & exit /b 1 )
echo.
pause
