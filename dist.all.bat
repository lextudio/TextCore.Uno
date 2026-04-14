@echo off
REM dist.all.bat - Build and sign LeXtudio.UI.Text.Core nupkg (Windows only)
SETLOCAL

:: Usage: dist.all.bat [Configuration]
if "%~1"=="" (
  set "CONFIG=Release"
) else (
  set "CONFIG=%~1"
)

echo Building and signing LeXtudio.UI.Text.Core (Configuration=%CONFIG%)

pushd "%~dp0"

REM Caller must provide either CERT_PFX_PATH and CERT_PFX_PASSWORD, or CERT_SUBJECT_NAME in the environment
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign-and-pack.ps1" -Configuration "%CONFIG%"
set EXITCODE=%ERRORLEVEL%
popd

if %EXITCODE% neq 0 exit /b %EXITCODE%
echo Done. Signed packages are generated.
ENDLOCAL
