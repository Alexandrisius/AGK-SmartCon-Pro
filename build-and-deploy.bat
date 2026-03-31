@echo off
chcp 65001 >nul
echo ==========================================
echo SmartCon - Build and Deploy
echo ==========================================

set "SLN_PATH=src\SmartCon.sln"
set "CONFIG=Debug"
set "FRAMEWORK=net8.0-windows"
set "REVIT_VERSION=2025"

echo.
echo [1/3] Building solution...
dotnet build "%SLN_PATH%" -c %CONFIG% --verbosity minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed!
    pause
    exit /b 1
)
echo [OK] Build successful

echo.
echo [2/3] Preparing deployment folder...
set "ADDIN_FOLDER=%APPDATA%\Autodesk\Revit\Addins\%REVIT_VERSION%"
set "SMARTCON_FOLDER=%ADDIN_FOLDER%\SmartCon"

if not exist "%SMARTCON_FOLDER%" mkdir "%SMARTCON_FOLDER%"

echo [OK] Folder ready: %SMARTCON_FOLDER%

echo.
echo [3/3] Deploying files...
copy /Y "src\SmartCon.App\bin\%CONFIG%\%FRAMEWORK%\*.dll" "%SMARTCON_FOLDER%\"
copy /Y "src\SmartCon.App\Resources\SmartCon.addin" "%ADDIN_FOLDER%\"

echo.
echo ==========================================
echo [SUCCESS] Deployed to Revit %REVIT_VERSION%
echo ==========================================
echo.
echo Files deployed:
dir /b "%SMARTCON_FOLDER%"
echo.
pause
