@echo off
setlocal
cd /d "%~dp0"
echo [KINOJO Meter] Verify GitHub Release
set /p GH_OWNER=GitHub owner: 
set /p GH_REPO=GitHub repository: 
set /p MIN_VERSION=Minimum supported version (example 0.2.18): 
set /p RELEASE_NOTE=Release note: 
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-github-release.ps1" -GitHubOwner "%GH_OWNER%" -GitHubRepository "%GH_REPO%" -MinimumVersion "%MIN_VERSION%" -ReleaseNote "%RELEASE_NOTE%" -VerifyRemote
if errorlevel 1 (
  echo.
  echo Remote verification failed.
  pause
  exit /b 1
)
echo.
echo GitHub Release verification completed.
pause
