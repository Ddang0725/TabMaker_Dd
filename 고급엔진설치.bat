@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0engine\install.ps1" %*
set "installResult=%errorlevel%"
if not "%installResult%"=="0" echo Installation failed. Existing engine settings were preserved.
if not "%~1"=="--check" pause
exit /b %installResult%
