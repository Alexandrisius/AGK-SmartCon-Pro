@echo off
chcp 65001 >nul
echo ==========================================
echo SmartCon - Build and Deploy (Debug)
echo ==========================================

set "SLN_PATH=src\SmartCon.sln"

echo.
echo [1/6] Restoring NuGet packages...
dotnet restore "%SLN_PATH%"
if errorlevel 1 (
    echo.
    echo [ERROR] Restore failed!
    pause
    exit /b 1
)
echo [OK] Restore successful

echo.
echo [2/6] Building Revit 2025 (net8.0)...
dotnet build "src\SmartCon.App\SmartCon.App.csproj" -c Debug -f net8.0-windows --no-restore --verbosity minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Build R25 failed!
    pause
    exit /b 1
)
echo [OK] R25 build successful

echo.
echo [3/6] Building Revit 2023 (net48)...
dotnet build "src\SmartCon.App\SmartCon.App.csproj" -c Debug -f net48 --no-restore --verbosity minimal -p:RevitVersion=2023
if errorlevel 1 (
    echo.
    echo [ERROR] Build R23 failed!
    pause
    exit /b 1
)
echo [OK] R23 build successful

echo.
echo [4/7] Building updater (net8.0)...
dotnet build "src\SmartCon.Updater\SmartCon.Updater.csproj" -c Debug -f net8.0 --no-restore --verbosity minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Updater build failed!
    pause
    exit /b 1
)
echo [OK] Updater build successful

echo.
echo [5/7] Deploying to Revit 2025...
set "ADDIN_R25=%APPDATA%\Autodesk\Revit\Addins\2025"
set "SMARTCON_R25=%APPDATA%\SmartCon\2025"
if not exist "%SMARTCON_R25%" mkdir "%SMARTCON_R25%"
copy /Y "src\SmartCon.App\bin\Debug\net8.0-windows\*.dll" "%SMARTCON_R25%\" >nul
copy /Y "src\SmartCon.App\bin\Debug\net8.0-windows\SmartCon.App.deps.json" "%SMARTCON_R25%\" >nul 2>nul
copy /Y "src\SmartCon.App\Resources\SmartCon-2025.addin" "%ADDIN_R25%\" >nul
echo [OK] Revit 2025 -^> %SMARTCON_R25%

echo.
echo [6/7] Deploying to Revit 2023...
set "ADDIN_R23=%APPDATA%\Autodesk\Revit\Addins\2023"
set "SMARTCON_R23=%APPDATA%\SmartCon\2021-2023"
if not exist "%SMARTCON_R23%" mkdir "%SMARTCON_R23%"
copy /Y "src\SmartCon.App\bin\Debug\net48\*.dll" "%SMARTCON_R23%\" >nul
copy /Y "src\SmartCon.App\Resources\SmartCon-2023.addin" "%ADDIN_R23%\" >nul
echo [OK] Revit 2023 -^> %SMARTCON_R23%

echo.
echo [7/7] Deploying updater...
set "UPDATER_SRC=src\SmartCon.Updater\bin\Debug\net8.0"
if not exist "%APPDATA%\SmartCon" mkdir "%APPDATA%\SmartCon"
copy /Y "%UPDATER_SRC%\SmartCon.Updater.exe" "%APPDATA%\SmartCon\" >nul 2>nul
copy /Y "%UPDATER_SRC%\SmartCon.Updater.dll" "%APPDATA%\SmartCon\" >nul 2>nul
copy /Y "%UPDATER_SRC%\SmartCon.Updater.deps.json" "%APPDATA%\SmartCon\" >nul 2>nul
copy /Y "%UPDATER_SRC%\SmartCon.Updater.runtimeconfig.json" "%APPDATA%\SmartCon\" >nul 2>nul
echo [OK] Updater -^> %APPDATA%\SmartCon\

echo.
echo ==========================================
echo [SUCCESS] Deployed to Revit 2023 + 2025
echo ==========================================
echo.
pause
