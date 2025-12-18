@echo off
echo ====================================
echo   Discord Music Bot Launcher
echo ====================================
echo.

cd /d "%~dp0"

echo [1/4] Checking for Java 17+...
java -version 2>&1 | findstr /C:"version \"17" >nul
if errorlevel 1 (
    java -version 2>&1 | findstr /C:"version \"18" >nul
    if errorlevel 1 (
        java -version 2>&1 | findstr /C:"version \"19" >nul
        if errorlevel 1 (
            java -version 2>&1 | findstr /C:"version \"20" >nul
            if errorlevel 1 (
                java -version 2>&1 | findstr /C:"version \"21" >nul
                if errorlevel 1 (
                    echo ERROR: Java 17 or higher is required!
                    echo Current Java version:
                    java -version
                    echo.
                    echo Please download Java 17+ from:
                    echo https://adoptium.net/
                    echo.
                    pause
                    exit /b 1
                )
            )
        )
    )
)
echo ✓ Java 17+ found

echo.
echo [2/4] Checking for Lavalink.jar...
if not exist "Lavalink.jar" (
    echo ERROR: Lavalink.jar not found!
    echo Please download Lavalink.jar from:
    echo https://github.com/freyacodes/Lavalink/releases/latest
    echo And place it in the same folder as this script.
    pause
    exit /b 1
)
echo ✓ Lavalink.jar found

echo.
echo [3/4] Checking for .NET...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET is not installed!
    echo Please install .NET 6.0 or higher from:
    echo https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
echo ✓ .NET found

echo.
echo [4/4] Starting Discord Music Bot...
echo Press Ctrl+C to stop the bot
echo ====================================
echo.
dotnet run

echo.
echo ====================================
echo Bot stopped.
pause
