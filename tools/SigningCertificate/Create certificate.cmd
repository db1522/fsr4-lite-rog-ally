@echo off
rem Double-click once to create the permanent FSR 4.1 Lite signing certificate. See create-signing-certificate.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-signing-certificate.ps1"
