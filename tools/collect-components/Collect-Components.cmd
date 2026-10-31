@echo off
rem Collect Inventor files by the list of names from the clipboard (see README.md).
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Collect-Components.ps1" -FromClipboard %*
pause
