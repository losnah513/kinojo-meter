@echo off
setlocal
title [KINOJO Meter] Clean Install Sandbox Test
echo [KINOJO Meter] Windows Sandbox clean-install test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\test-clean-install-sandbox.ps1"
if errorlevel 1 (
  echo.
  echo Test launch failed. Review the message above.
  pause
  exit /b 1
)
echo.
echo Test workflow completed.
pause
