@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
echo ==========================================
echo SmartCon - Build and Deploy (Debug)
echo ==========================================
echo.

set "SLN_PATH=src\SmartCon.sln"

echo [1/9] Restoring NuGet packages...
dotnet restore "%SLN_PATH%" --verbosity quiet
if errorlevel 1 ( echo [ERROR] Restore failed! & pause & exit /b 1 )
echo [OK] Restore successful

echo.
echo [2/9] Building Revit 2025 (net8.0)...
dotnet build "src\SmartCon.App\SmartCon.App.csproj" -c Debug -f net8.0-windows --no-restore --verbosity quiet
if errorlevel 1 ( echo [ERROR] Build R25 failed! & pause & exit /b 1 )
echo [OK] R25 build successful

echo.
echo [3/9] Building Revit 2023 (net48, R21)...
dotnet build "src\SmartCon.App\SmartCon.App.csproj" -c Debug -f net48 --no-restore --verbosity quiet -p:RevitVersion=2023
if errorlevel 1 ( echo [ERROR] Build R23 failed! & pause & exit /b 1 )
echo [OK] R23 build successful

echo.
echo [4/9] Building Revit 2019-2020 (net48, R19)...
dotnet build "src\SmartCon.App\SmartCon.App.csproj" -c Debug -f net48 --no-restore --verbosity quiet -p:RevitVersion=2019
if errorlevel 1 ( echo [ERROR] Build R19 failed! & pause & exit /b 1 )
echo [OK] R19 build successful

echo.
echo [5/9] Building updater (net8.0)...
dotnet build "src\SmartCon.Updater\SmartCon.Updater.csproj" -c Debug -f net8.0 --no-restore --verbosity quiet
if errorlevel 1 ( echo [ERROR] Updater build failed! & pause & exit /b 1 )
echo [OK] Updater build successful

echo.
echo [6/9] Deploying to Revit 2025...
set "ADDIN_R25=%APPDATA%\Autodesk\Revit\Addins\2025"
set "DLL_R25=%APPDATA%\SmartCon\2025"
if exist "%ADDIN_R25%\SmartCon\SmartCon.App.dll" ( rd /s /q "%ADDIN_R25%\SmartCon" 2>nul )
if not exist "%DLL_R25%" mkdir "%DLL_R25%"
copy /Y "src\SmartCon.App\bin\Debug\net8.0-windows\*.dll" "%DLL_R25%\" >nul
copy /Y "src\SmartCon.App\bin\Debug\net8.0-windows\SmartCon.App.deps.json" "%DLL_R25%\" >nul 2>nul
call :WriteAddin "%ADDIN_R25%\SmartCon.addin" "%DLL_R25%\SmartCon.App.dll"
echo [OK] Revit 2025

echo.
echo [7/9] Deploying to Revit 2023...
set "ADDIN_R23=%APPDATA%\Autodesk\Revit\Addins\2023"
set "DLL_R23=%APPDATA%\SmartCon\2021-2023"
if exist "%ADDIN_R23%\SmartCon\SmartCon.App.dll" ( rd /s /q "%ADDIN_R23%\SmartCon" 2>nul )
if exist "%ADDIN_R23%\SmartCon-2023.addin" del /q "%ADDIN_R23%\SmartCon-2023.addin" 2>nul
if not exist "%DLL_R23%" mkdir "%DLL_R23%"
copy /Y "src\SmartCon.App\bin\Debug\net48\*.dll" "%DLL_R23%\" >nul
call :WriteAddin "%ADDIN_R23%\SmartCon.addin" "%DLL_R23%\SmartCon.App.dll"
echo [OK] Revit 2023

echo.
echo [8/9] Deploying to Revit 2019-2020...
set "DLL_R19=%APPDATA%\SmartCon\2019-2020"
if not exist "%DLL_R19%" mkdir "%DLL_R19%"
copy /Y "src\SmartCon.App\bin\Debug.R19\net48\*.dll" "%DLL_R19%\" >nul
set "ADDIN_R19=%APPDATA%\Autodesk\Revit\Addins\2019"
if exist "%ADDIN_R19%" (
    if not exist "%ADDIN_R19%" mkdir "%ADDIN_R19%"
    call :WriteAddin "%ADDIN_R19%\SmartCon.addin" "%DLL_R19%\SmartCon.App.dll"
    echo [OK] Revit 2019
) else (
    echo [SKIP] Revit 2019 not installed
)
set "ADDIN_R20=%APPDATA%\Autodesk\Revit\Addins\2020"
if exist "%ADDIN_R20%" (
    if not exist "%ADDIN_R20%" mkdir "%ADDIN_R20%"
    call :WriteAddin "%ADDIN_R20%\SmartCon.addin" "%DLL_R19%\SmartCon.App.dll"
    echo [OK] Revit 2020
) else (
    echo [SKIP] Revit 2020 not installed
)

echo.
echo [9/9] Deploying updater...
set "UPDATER_SRC=src\SmartCon.Updater\bin\Debug\net8.0"
copy /Y "%UPDATER_SRC%\SmartCon.Updater.exe" "%APPDATA%\SmartCon\" >nul 2>nul
copy /Y "%UPDATER_SRC%\SmartCon.Updater.dll" "%APPDATA%\SmartCon\" >nul 2>nul
copy /Y "%UPDATER_SRC%\SmartCon.Updater.deps.json" "%APPDATA%\SmartCon\" >nul 2>nul
copy /Y "%UPDATER_SRC%\SmartCon.Updater.runtimeconfig.json" "%APPDATA%\SmartCon\" >nul 2>nul
echo [OK] Updater

echo.
echo ==========================================
echo [SUCCESS] Deployed to all Revit versions
echo ==========================================
echo.
echo  2019-2020: %DLL_R19%
echo  2021-2023: %DLL_R23%
echo  2025: %DLL_R25%
echo.
pause
exit /b 0

:WriteAddin
echo ^<?xml version="1.0" encoding="utf-8"?^> > "%~1"
echo ^<RevitAddIns^> >> "%~1"
echo   ^<AddIn Type="Application"^> >> "%~1"
echo     ^<Name^>SmartCon^</Name^> >> "%~1"
echo     ^<Assembly^>%~2^</Assembly^> >> "%~1"
echo     ^<AddInId^>A1B2C3D4-E5F6-7890-ABCD-EF1234567890^</AddInId^> >> "%~1"
echo     ^<FullClassName^>SmartCon.App.App^</FullClassName^> >> "%~1"
echo     ^<VendorId^>AGK^</VendorId^> >> "%~1"
echo     ^<VendorDescription^>AGK Engineering^</VendorDescription^> >> "%~1"
echo   ^</AddIn^> >> "%~1"
echo ^</RevitAddIns^> >> "%~1"
goto :eof
