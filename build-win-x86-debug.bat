@echo off
echo ========================================
echo  Win x86 (32-bit) ^| DEBUG
echo ========================================
echo.
dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: .NET 8 SDK not found. & pause & exit /b 1 )
dotnet restore
dotnet publish -c Debug -r win-x86 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish\win-x86-debug
if exist publish\win-x86-debug\LustsDepotDownloaderPro.exe (
    echo.
    echo [OK] publish\win-x86-debug\LustsDepotDownloaderPro.exe
    echo [OK] PDB included for debugging
) else ( echo [FAIL] Build failed. & pause & exit /b 1 )
echo.
pause
