@echo off
color 0b
echo ==========================================
echo        KHOI DONG DEPLOY SCRIPT...
echo ==========================================
powershell.exe -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"
echo.
pause
