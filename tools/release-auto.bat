@echo off
chcp 65001 >nul 2>&1
title SmartCon Auto-Release (Patch++)

echo.
echo  [SmartCon] Auto-release: patch++
echo.

powershell -ExecutionPolicy Bypass -NoProfile -Command ^
    "& '%~dp0release.ps1' -AutoIncrement; exit $LASTEXITCODE"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [ERROR] Release failed! See output above.
    pause
    exit /b 1
)

echo.
pause
