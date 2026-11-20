@echo off
rem Double-click to build and install Alliance Status for the current user. No admin rights needed.
title Installing Alliance Status
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1"
echo.
pause
