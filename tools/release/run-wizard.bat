@echo off
REM Wrapper to run the release wizard with correct quoting
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0wizard.ps1'" %*