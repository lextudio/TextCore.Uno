@echo off
pwsh -ExecutionPolicy Bypass -file sign-and-pack.ps1 %*
if %errorlevel% neq 0 exit /b %errorlevel%
