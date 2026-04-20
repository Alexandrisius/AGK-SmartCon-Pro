@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion
title SmartCon Release

echo.
echo  ============================================
echo         SmartCon - One-Click Release
echo  ============================================
echo.

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."

for /f "tokens=* delims=" %%a in ('type "%ROOT_DIR%\Version.txt"') do set "CURRENT_VER=%%a"

echo  Current version: v%CURRENT_VER%
echo.
echo  Select release type:
echo.
echo    [1]  Patch     - auto-increment patch
echo    [2]  Minor     - increment minor, reset patch
echo    [3]  Major     - increment major, reset rest
echo    [4]  Custom    - enter version manually
echo    [5]  Dry Run   - preflight check without tag/push/release
echo    [Q]  Quit
echo.

set /p "CHOICE=  Your choice (1/2/3/4/5/Q): "

if /i "%CHOICE%"=="Q" goto :eof
if /i "%CHOICE%"=="q" goto :eof

if "%CHOICE%"=="1" (
    echo.
    echo  ==^> Patch release...
    powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%release.ps1" -AutoIncrement
    if errorlevel 1 goto :failed
    goto :done
)
if "%CHOICE%"=="2" (
    echo.
    echo  ==^> Minor release...
    powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%release.ps1" -MinorIncrement
    if errorlevel 1 goto :failed
    goto :done
)
if "%CHOICE%"=="3" (
    echo.
    echo  ==^> Major release...
    powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%release.ps1" -MajorIncrement
    if errorlevel 1 goto :failed
    goto :done
)
if "%CHOICE%"=="4" (
    set /p "CUSTOM_VER=  Enter version (e.g. 1.2.0): "
    if "!CUSTOM_VER!"=="" (
        echo  ERROR: Empty version.
        pause
        goto :eof
    )
    echo.
    echo  ==^> Custom release v!CUSTOM_VER!...
    powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%release.ps1" -Version "!CUSTOM_VER!"
    if errorlevel 1 goto :failed
    goto :done
)
if "%CHOICE%"=="5" (
    echo.
    echo  ==^> Dry run preflight...
    powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%release.ps1" -AutoIncrement -DryRun
    if errorlevel 1 goto :failed
    goto :done
)

echo  Invalid choice.
pause
goto :eof

:failed
echo.
echo  ERROR: Release command failed.
pause
goto :eof

:done
echo.
pause
