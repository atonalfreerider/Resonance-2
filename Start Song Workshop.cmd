@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\SongLibrary\song-library.ps1"
if errorlevel 1 pause
