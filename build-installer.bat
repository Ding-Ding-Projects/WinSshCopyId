@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" -Action installer %*
exit /b %errorlevel%
