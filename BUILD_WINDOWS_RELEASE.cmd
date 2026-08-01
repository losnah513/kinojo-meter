@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "METER_DIR=%~dp0"
for %%I in ("%METER_DIR%..") do set "DEFAULT_PROJECT_ROOT=%%~fI"

echo [KINOJO Meter] Windows Release build
echo Current project root: %DEFAULT_PROJECT_ROOT%
set "PROJECT_ROOT=%~1"
if not defined PROJECT_ROOT set /p "PROJECT_ROOT=Project root path (Enter = current): "
if not defined PROJECT_ROOT set "PROJECT_ROOT=%DEFAULT_PROJECT_ROOT%"

if exist "%PROJECT_ROOT%\05_METER_DESKTOP\scripts\build-windows.ps1" goto :root_ok
if exist "%PROJECT_ROOT%\scripts\build-windows.ps1" (
    for %%I in ("%PROJECT_ROOT%\..") do set "PROJECT_ROOT=%%~fI"
    goto :root_ok
)

echo.
echo KINOJO Meter was not found under this path:
echo %PROJECT_ROOT%
echo Expected: ^<project root^>\05_METER_DESKTOP
pause
exit /b 1

:root_ok
echo Using project root: %PROJECT_ROOT%
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%METER_DIR%scripts\build-windows.ps1" -Configuration Release -ProjectRoot "%PROJECT_ROOT%"
if errorlevel 1 (
  echo.
  echo Build failed. Review the error message above.
  pause
  exit /b 1
)

echo.
echo Build completed. Check the 05_METER_DESKTOP\build folder.
pause
