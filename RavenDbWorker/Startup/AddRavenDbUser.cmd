@echo off
REM Nicht im Emulator ausfuehren
if "%TestIsEmulated%"=="true" goto :EOF

net user %RavenUserName% >nul 2>&1 && net user %RavenUserName% %RavenPassword% >> log.txt 2>> err.txt || net user %RavenUserName% %RavenPassword% /ADD >> log.txt 2>> err.txt