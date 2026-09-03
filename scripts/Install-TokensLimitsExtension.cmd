@echo off
setlocal

rem ExecutionPolicy is a PowerShell policy, not a signature check for the MSIX.
rem The helper script elevates through UAC before trusting the public certificate.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TokensLimitsExtension.ps1"
exit /b %ERRORLEVEL%
