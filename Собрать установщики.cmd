@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\build-release.ps1"
if errorlevel 1 (
  echo.
  echo Сборка не завершена. Причина указана выше.
)
pause
