@echo off
REM dist.publish2nugetdotorg.bat - Push signed nupkgs to NuGet.org
SETLOCAL ENABLEDELAYEDEXPANSION

:: Usage: dist.publish2nugetdotorg.bat [nupkgFolder]
if "%~1"=="" (
  set "PKGDIR=%~dist"
) else (
  set "PKGDIR=%~1"
)

pushd "%~dp0"

:: locate nuget.exe: prefer tools\nuget.exe, otherwise require nuget on PATH
if exist "tools\nuget.exe" (
  set "NUGETEXE=%~dp0tools\nuget.exe"
) else (
  where nuget.exe >nul 2>&1
  if errorlevel 1 (
    echo ERROR: nuget.exe not found in PATH and not present in tools\.
    echo Please download nuget.exe from https://www.nuget.org/downloads and place it in tools\ or add to PATH.
    popd
    exit /b 1
  ) else (
    for /f "delims=" %%i in ('where nuget.exe') do set "NUGETEXE=%%i" & goto :foundnuget
  )
)
:foundnuget
echo Using NuGet: %NUGETEXE%

if not exist "%PKGDIR%\*.nupkg" (
  echo No .nupkg files found in %PKGDIR%
  popd
  exit /b 1
)

for %%F in ("%PKGDIR%\*.nupkg") do (
  echo Pushing %%~fF
  "%NUGETEXE%" push "%%~fF" -Source "https://api.nuget.org/v3/index.json" -NonInteractive -Verbosity detailed
  if errorlevel 1 (
    echo Failed to push %%~fF
    popd
    exit /b 1
  )
)

echo All packages pushed to nuget.org.
popd
endlocal
exit /b 0
