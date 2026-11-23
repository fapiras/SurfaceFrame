@echo off
setlocal
if not exist "%~dp0setup.ps1" (
  echo Estrai tutto lo ZIP e avvia questo file dalla cartella estratta.
  pause
  exit /b 1
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
if errorlevel 1 (
  pause
  exit /b 1
)
