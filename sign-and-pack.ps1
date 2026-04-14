param(
    [string]$Configuration = "Release",
    [string]$OutputDir = ".\\dist",
    [string]$CertSubjectName = $env:CERT_SUBJECT_NAME,
    [string]$CertPfxPath = $env:CERT_PFX_PATH,
    [string]$CertPfxPassword = $env:CERT_PFX_PASSWORD,
    [string]$NugetExePath = "",
    [string]$SigntoolPath = "",
    [string]$Timestamper = "http://timestamp.digicert.com"
)

function Write-ErrorAndExit($msg, $code = 1) {
    Write-Error $msg
    exit $code
}

Write-Host "Sign-and-pack: Configuration=$Configuration Output=$OutputDir"

# Windows-only script
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    Write-ErrorAndExit "This packaging/signing script supports Windows only."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

if (Test-Path $OutputDir) { Remove-Item -Path (Join-Path $OutputDir "*.nupkg") -ErrorAction SilentlyContinue }
New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

# Locate the project file under the current folder
$projectFile = Get-ChildItem -Path $scriptRoot -Filter '*.csproj' -Recurse -File | Where-Object { $_.Name -like 'LeXtudio.UI.Text.Core*.csproj' } | Select-Object -First 1
if ($null -eq $projectFile) {
    $projectFile = Get-ChildItem -Path $scriptRoot -Filter '*.csproj' -Recurse -File | Select-Object -First 1
}
if ($null -eq $projectFile) { Write-ErrorAndExit "No .csproj found under $scriptRoot" }

$projectDir = Split-Path -Parent $projectFile.FullName

function Get-GitRoot($startPath) {
    $p = Resolve-Path -Path $startPath
    $dir = $p.Path
    while ($dir -ne [System.IO.Path]::GetPathRoot($dir)) {
        if (Test-Path (Join-Path $dir '.git')) { return $dir }
        $dir = Split-Path -Parent $dir
    }
    return $null
}

# Ensure repository-level obj folder exists because GitVersion writes to <repo>\obj\gitversion.json
$repoRoot = Get-GitRoot $projectDir
if ($repoRoot) {
    $repoObj = Join-Path $repoRoot 'obj'
    if (-not (Test-Path $repoObj)) { New-Item -Path $repoObj -ItemType Directory -Force | Out-Null }
}

Write-Host "Building project $($projectFile.FullName) (dotnet build -c $Configuration)"
Push-Location $projectDir
& dotnet build $projectFile.FullName -c $Configuration
if ($LASTEXITCODE -ne 0) { Pop-Location; Write-ErrorAndExit "dotnet build failed (exit $LASTEXITCODE)." }
Pop-Location

# Locate signtool.exe
if ([string]::IsNullOrEmpty($SigntoolPath)) {
    $st = Get-Command signtool -ErrorAction SilentlyContinue
    if ($st) { $SigntoolPath = $st.Source }
    else {
        $found = Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($found) { $SigntoolPath = $found.FullName }
    }
}
if (-not (Test-Path $SigntoolPath)) { Write-ErrorAndExit "signtool.exe not found. Install Windows SDK or set -SigntoolPath to the signtool.exe path." }

# Locate or download nuget.exe
if ([string]::IsNullOrEmpty($NugetExePath)) {
    $nugetCmd = Get-Command nuget -ErrorAction SilentlyContinue
    if ($nugetCmd) { $NugetExePath = $nugetCmd.Source }
    else {
        $toolsDir = Join-Path $scriptRoot 'tools'
        New-Item -Path $toolsDir -ItemType Directory -Force | Out-Null
        $localNuget = Join-Path $toolsDir 'nuget.exe'
        if (-not (Test-Path $localNuget)) {
            Write-Host "Downloading nuget.exe to $localNuget..."
            try { Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $localNuget -UseBasicParsing -ErrorAction Stop }
            catch { Write-ErrorAndExit "Failed to download nuget.exe: $_" }
        }
        $NugetExePath = $localNuget
    }
}
if (-not (Test-Path $NugetExePath)) { Write-ErrorAndExit "nuget.exe not found and could not be downloaded. Set -NugetExePath to a valid nuget.exe." }

# Determine certificate selection method
$usePfx = $false
if (-not [string]::IsNullOrEmpty($CertPfxPath)) {
    if (-not (Test-Path $CertPfxPath)) { Write-ErrorAndExit "Certificate PFX path not found: $CertPfxPath" }
    $usePfx = $true
} elseif (-not [string]::IsNullOrEmpty($CertSubjectName)) {
    $usePfx = $false
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
    if ($null -eq $cert) { Write-ErrorAndExit "No code-signing certificate found in Cert:\CurrentUser\My and no CERT_PFX_PATH set." }
    $CertSubjectName = $cert.Subject
    $usePfx = $false
}

if ($usePfx) { $signingMethod = "PFX: $CertPfxPath" } else { $signingMethod = "Store Subject: $CertSubjectName (CurrentUser\\My)" }
Write-Host "Signing method: $signingMethod"

# Locate build output TFM folder under project bin\$Configuration
$binCfg = Join-Path $projectDir "bin\$Configuration"
if (-not (Test-Path $binCfg)) { Write-ErrorAndExit "Build output directory not found: $binCfg" }

$tfmFolder = Get-ChildItem -Path $binCfg -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'net10*' -or $_.Name -eq 'net10.0-desktop' } | Select-Object -First 1
if ($null -eq $tfmFolder) { $tfmFolder = Get-ChildItem -Path $binCfg -Directory -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($null -eq $tfmFolder) { Write-ErrorAndExit "Unable to determine build TFM directory under $binCfg" }

$signRoot = $tfmFolder.FullName
Write-Host "Signing binaries under: $signRoot"

$filesToSign = Get-ChildItem -Path $signRoot -Recurse -File | Where-Object { ($_.Extension -ieq '.dll') -or ($_.Extension -ieq '.exe') } | Sort-Object FullName
if ($filesToSign.Count -eq 0) { Write-Warning "No .dll/.exe files found to sign under $signRoot" }

foreach ($file in $filesToSign) {
    Write-Host "Signing $($file.FullName)"
    if ($usePfx) {
        & "$SigntoolPath" sign /fd SHA256 /f "$CertPfxPath" /p "$CertPfxPassword" /tr $Timestamper /td SHA256 /v "$($file.FullName)"
    } else {
        & "$SigntoolPath" sign /fd SHA256 /n "$CertSubjectName" /s My /tr $Timestamper /td SHA256 /v "$($file.FullName)"
    }
    if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "signtool failed signing $($file.FullName) (exit $LASTEXITCODE)" }
}

# Create nupkg from project (no-build because we already built and signed the outputs)
Write-Host "Packing nupkg files (dotnet pack $($projectFile.FullName) -c $Configuration -o $OutputDir --no-build)"
Push-Location $projectDir
& dotnet pack $projectFile.FullName -c $Configuration -o $OutputDir --no-build
if ($LASTEXITCODE -ne 0) { Pop-Location; Write-ErrorAndExit "dotnet pack failed (exit $LASTEXITCODE)" }
Pop-Location

# Sign produced nupkg files with nuget.exe
$nupkgs = Get-ChildItem -Path $OutputDir -Filter '*.nupkg' -File
if ($nupkgs.Count -eq 0) { Write-ErrorAndExit "No packages found in $OutputDir" }

foreach ($pkg in $nupkgs) {
    Write-Host "Signing package $($pkg.FullName)"
    if ($usePfx) {
        & "$NugetExePath" sign "$($pkg.FullName)" -CertificatePath "$CertPfxPath" -CertificatePassword "$CertPfxPassword" -Timestamper $Timestamper -Verbosity detailed
    } else {
        & "$NugetExePath" sign "$($pkg.FullName)" -CertificateSubjectName "$CertSubjectName" -CertificateStoreLocation CurrentUser -CertificateStoreName My -Timestamper $Timestamper -Verbosity detailed
    }
    if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "nuget.exe sign failed for $($pkg.FullName) (exit $LASTEXITCODE)" }

    Write-Host "Verifying package $($pkg.FullName)"
    & "$NugetExePath" verify -All "$($pkg.FullName)"
    if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "nuget.exe verify failed for $($pkg.FullName) (exit $LASTEXITCODE)" }
}

Write-Host "All packages signed and verified. Packages located in: $OutputDir"
