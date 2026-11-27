@echo off
title LIGClaw Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LIGClaw.ps1"
if errorlevel 1 exit /b 1
