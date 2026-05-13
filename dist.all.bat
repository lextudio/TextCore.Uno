@echo off
setlocal enabledelayedexpansion

REM dist.all.bat — Build, pack, and sign all nupkgs on Windows.
REM Expects the LeXtudio.UI.Text.Core nupkg to already be present in dist\
REM (copy it there from macOS before running this script).
REM Builds both the Uno desktop and WinUI targets of LeXtudio.TextBox, packs it
REM into dist\, then signs all packages found in dist\.
REM Usage: dist.all.bat [Configuration] [PackageVersion]
REM Example: dist.all.bat Release 0.2.1

set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

set PKGVER=%~2
if "%PKGVER%"=="" set PKGVER=0.2.1

set PROJECT=src\LeXtudio.TextBox\LeXtudio.TextBox.csproj
set OUT_DIR=%SCRIPT_DIR%dist

echo dist.all.bat: Configuration=%CONFIG% PackageVersion=%PKGVER%

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

echo Restoring packages...
dotnet restore "%PROJECT%"
if errorlevel 1 exit /b 1

echo Packing nupkg to %OUT_DIR%...
dotnet pack "%PROJECT%" -c %CONFIG% -o "%OUT_DIR%" /p:PackageVersion=%PKGVER%
if errorlevel 1 exit /b 1

echo.
echo Pack output:
dir "%OUT_DIR%"

echo.
echo Signing all packages in %OUT_DIR%...
if not exist "%SCRIPT_DIR%sign-existing-nupkgs.ps1" (
  echo Warning: sign-existing-nupkgs.ps1 not found; skipping signing.
  goto :done
)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%sign-existing-nupkgs.ps1" -PackagesDir "%OUT_DIR%" -PreferDotnetSign
if errorlevel 1 (
  echo Signing failed.
  exit /b 1
)

:done
echo.
echo Done. Signed packages are in %OUT_DIR%
endlocal
