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

# Normalize OutputDir: make absolute and anchored to the script root when relative
if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $scriptRoot $OutputDir
}
try {
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
} catch {
    Write-ErrorAndExit "Invalid OutputDir path: $OutputDir"
}

# Ensure directory exists and remove any existing nupkgs
New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
Remove-Item -Path (Join-Path $OutputDir "*.nupkg") -ErrorAction SilentlyContinue -Force

# Locate the project file under the current folder
# Prefer the exact library project and avoid selecting sample projects
$allCsprojs = Get-ChildItem -Path $scriptRoot -Filter '*.csproj' -Recurse -File

# 1) Prefer exact `LeXtudio.UI.Text.Core.csproj`
$projectFile = $allCsprojs | Where-Object { $_.Name -ieq 'LeXtudio.UI.Text.Core.csproj' } | Select-Object -First 1

# 2) Otherwise prefer a core project that is not a sample (e.g. avoid names containing 'Sample')
if ($null -eq $projectFile) {
    $projectFile = $allCsprojs | Where-Object { $_.Name -like 'LeXtudio.UI.Text.Core*.csproj' -and ($_.Name -notlike '*Sample*') } | Select-Object -First 1
}

# 3) Fallback to any non-sample project
if ($null -eq $projectFile) {
    $projectFile = $allCsprojs | Where-Object { $_.Name -notlike '*Sample*' } | Select-Object -First 1
}

# 4) As last resort, pick the first project found
if ($null -eq $projectFile) {
    $projectFile = $allCsprojs | Select-Object -First 1
}

if ($null -eq $projectFile) { Write-ErrorAndExit "No .csproj found under $scriptRoot" }

$projectDir = Split-Path -Parent $projectFile.FullName

Write-Host "Building project $($projectFile.FullName) (dotnet build -c $Configuration)"
Push-Location $projectDir
& dotnet build $projectFile.FullName -c $Configuration
if ($LASTEXITCODE -ne 0) { Pop-Location; Write-ErrorAndExit "dotnet build failed (exit $LASTEXITCODE)." }
Pop-Location

# Locate signtool.exe — prefer explicit path, otherwise use the system `signtool` on PATH
$signtool = $null
if (-not [string]::IsNullOrEmpty($SigntoolPath)) {
    try { $signtool = (Resolve-Path -Path $SigntoolPath -ErrorAction SilentlyContinue).ProviderPath } catch { $signtool = $SigntoolPath }
} else {
    $st = Get-Command signtool -ErrorAction SilentlyContinue
    if ($st) { $signtool = $st.Source }
}

if (-not $signtool -or -not (Test-Path $signtool)) {
    # Try a minimal fallback: look under common Windows Kits locations for a single signtool.exe
    $kitPaths = @(
        'C:\Program Files (x86)\Windows Kits\10\bin',
        'C:\Program Files\Windows Kits\10\bin',
        'C:\Program Files (x86)\Windows Kits\8.1\bin',
        'C:\Program Files\Windows Kits\8.1\bin'
    )
    foreach ($kp in $kitPaths) {
        if (Test-Path $kp) {
            try {
                $found = Get-ChildItem -Path $kp -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($found) { $signtool = $found.FullName; break }
            } catch { }
        }
    }
}

if (-not $signtool -or -not (Test-Path $signtool)) { Write-ErrorAndExit "signtool.exe not found. Install Windows SDK or set -SigntoolPath to the signtool.exe path." }

# Locate or download nuget.exe
function Get-PE-Arch($path) {
    try {
        $fs = [System.IO.File]::OpenRead($path)
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Seek(0x3c, 'Begin') | Out-Null
        $peOffset = $br.ReadInt32()
        $fs.Seek($peOffset + 4, 'Begin') | Out-Null
        $machine = $br.ReadUInt16()
        $br.Close(); $fs.Close()
        switch ($machine) {
            0x014c { return 'x86' }
            0x8664 { return 'x64' }
            0xAA64 { return 'arm64' }
            default { return "unknown(0x{0:X4})" -f $machine }
        }
    } catch { return 'unknown' }
}

if ([string]::IsNullOrEmpty($NugetExePath)) {
    $nugetCmd = Get-Command nuget -ErrorAction SilentlyContinue
    if ($nugetCmd) {
        $NugetExePath = $nugetCmd.Source
        Write-Host "Found nuget on PATH: $NugetExePath"
    } else {
        $toolsDir = Join-Path $scriptRoot 'tools'
        New-Item -Path $toolsDir -ItemType Directory -Force | Out-Null
        $localNuget = Join-Path $toolsDir 'nuget.exe'

        # Prefer x64 nuget on 64-bit OS (helps when private key provider is 64-bit only)
        if ([System.Environment]::Is64BitOperatingSystem) {
            $nugetUrl = 'https://dist.nuget.org/win-x64-commandline/latest/nuget.exe'
        } else {
            $nugetUrl = 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe'
        }

        if (-not (Test-Path $localNuget)) {
            Write-Host "Downloading nuget.exe to $localNuget... (from $nugetUrl)"
            try { Invoke-WebRequest -Uri $nugetUrl -OutFile $localNuget -UseBasicParsing -ErrorAction Stop }
            catch { Write-ErrorAndExit "Failed to download nuget.exe: $_" }
        } else {
            # If existing is x86 on a 64-bit OS, replace it with x64 to avoid KSP/CSP provider mismatches
            if ([System.Environment]::Is64BitOperatingSystem) {
                $arch = Get-PE-Arch $localNuget
                if ($arch -eq 'x86') {
                    Write-Host "$localNuget appears to be x86 on an x64 OS — replacing with x64 nuget.exe"
                    try { Invoke-WebRequest -Uri $nugetUrl -OutFile $localNuget -UseBasicParsing -ErrorAction Stop } catch { Write-Warning "Failed to replace existing nuget.exe: $_" }
                }
            }
        }
        $NugetExePath = $localNuget
    }
}
if (-not (Test-Path $NugetExePath)) { Write-ErrorAndExit "nuget.exe not found and could not be downloaded. Set -NugetExePath to a valid nuget.exe." }

## Determine certificate selection method (prefer Code Signing cert like JexusManager does)
$usePfx = $false
$CertThumbprint = $null
$CertStoreLocation = $null

if (-not [string]::IsNullOrEmpty($CertPfxPath)) {
    if (-not (Test-Path $CertPfxPath)) { Write-ErrorAndExit "Certificate PFX path not found: $CertPfxPath" }
    $usePfx = $true
} else {
    # 1) Prefer an explicit Code Signing certificate from CurrentUser
    try { $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Select-Object -First 1 } catch { $cert = $null }
    if ($null -ne $cert) {
        $CertThumbprint = $cert.Thumbprint
        $CertSubjectName = $cert.Subject
        $CertStoreLocation = 'CurrentUser'
    }

    # 2) If none, try LocalMachine store for Code Signing cert
    if ($null -eq $CertThumbprint) {
        try { $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue | Select-Object -First 1 } catch { $cert = $null }
        if ($null -ne $cert) {
            $CertThumbprint = $cert.Thumbprint
            $CertSubjectName = $cert.Subject
            $CertStoreLocation = 'LocalMachine'
        }
    }

    # 3) If user provided a CERT_SUBJECT_NAME, try to match a Code Signing cert by subject or thumbprint
    if ($null -eq $CertThumbprint -and -not [string]::IsNullOrEmpty($CertSubjectName)) {
        try { $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { ($_.Subject -like "*${CertSubjectName}*") -or ($_.Thumbprint -eq $CertSubjectName) } | Select-Object -First 1 } catch { $cert = $null }
        if ($null -ne $cert) { $CertThumbprint = $cert.Thumbprint; $CertSubjectName = $cert.Subject; $CertStoreLocation = 'CurrentUser' }

        if ($null -eq $CertThumbprint) {
            try { $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { ($_.Subject -like "*${CertSubjectName}*") -or ($_.Thumbprint -eq $CertSubjectName) } | Select-Object -First 1 } catch { $cert = $null }
            if ($null -ne $cert) { $CertThumbprint = $cert.Thumbprint; $CertSubjectName = $cert.Subject; $CertStoreLocation = 'LocalMachine' }
        }
    }

    # 4) As a last resort, if no Code Signing cert found, fall back to any cert with a private key (CurrentUser then LocalMachine)
    if ($null -eq $CertThumbprint) {
        try { $cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object { $_.HasPrivateKey } | Select-Object -First 1 } catch { $cert = $null }
        if ($null -ne $cert) { $CertThumbprint = $cert.Thumbprint; $CertSubjectName = $cert.Subject; $CertStoreLocation = 'CurrentUser' }
        else {
            try { $cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.HasPrivateKey } | Select-Object -First 1 } catch { $cert = $null }
            if ($null -ne $cert) { $CertThumbprint = $cert.Thumbprint; $CertSubjectName = $cert.Subject; $CertStoreLocation = 'LocalMachine' }
        }
    }

    if ($null -eq $CertThumbprint) {
        Write-ErrorAndExit "No certificate with a private key found; set CERT_PFX_PATH or install a Code Signing certificate in the store."
    }
    $usePfx = $false
}

if ($usePfx) { $signingMethod = "PFX: $CertPfxPath" }
else { $signingMethod = "Store Thumbprint: $CertThumbprint ($CertStoreLocation\\My)" }
Write-Host "Signing method: $signingMethod"

# Prepare a nuget-friendly certificate subject (nuget.exe expects the common name)
$NugetCertSubjectName = $null
if (-not $usePfx -and -not [string]::IsNullOrEmpty($CertSubjectName)) {
    $m = [regex]::Match($CertSubjectName, 'CN=([^,]+)')
    if ($m.Success) { $NugetCertSubjectName = $m.Groups[1].Value.Trim() } else { $NugetCertSubjectName = $CertSubjectName }
}

# Locate build output TFM folder under project bin\$Configuration
$binCfg = Join-Path $projectDir "bin\$Configuration"
if (-not (Test-Path $binCfg)) { Write-ErrorAndExit "Build output directory not found: $binCfg" }

$tfmFolder = Get-ChildItem -Path $binCfg -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'net9*' -or $_.Name -eq 'net9.0-desktop' } | Select-Object -First 1
if ($null -eq $tfmFolder) { $tfmFolder = Get-ChildItem -Path $binCfg -Directory -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($null -eq $tfmFolder) { Write-ErrorAndExit "Unable to determine build TFM directory under $binCfg" }

$signRoot = $tfmFolder.FullName
Write-Host "Signing binaries under: $signRoot"

$filesToSign = Get-ChildItem -Path $signRoot -Recurse -File | Where-Object { ($_.Extension -ieq '.dll') -or ($_.Extension -ieq '.exe') } | Sort-Object FullName
if ($filesToSign.Count -eq 0) { Write-Warning "No .dll/.exe files found to sign under $signRoot" }

foreach ($file in $filesToSign) {
    Write-Host "Signing $($file.FullName)"
    try {
        if ($usePfx) {
            & "$signtool" sign /fd SHA256 /f "$CertPfxPath" /p "$CertPfxPassword" /tr $Timestamper /td SHA256 /v "$($file.FullName)"
        } else {
            if (-not [string]::IsNullOrEmpty($CertThumbprint)) {
                if ($CertStoreLocation -eq 'LocalMachine') {
                    & "$signtool" sign /fd SHA256 /sha1 $CertThumbprint /s My /sm /tr $Timestamper /td SHA256 /v "$($file.FullName)"
                } else {
                    & "$signtool" sign /fd SHA256 /sha1 $CertThumbprint /s My /tr $Timestamper /td SHA256 /v "$($file.FullName)"
                }
            } else {
                if ($CertStoreLocation -eq 'LocalMachine') {
                    & "$signtool" sign /fd SHA256 /n "$CertSubjectName" /s My /sm /tr $Timestamper /td SHA256 /v "$($file.FullName)"
                } else {
                    & "$signtool" sign /fd SHA256 /n "$CertSubjectName" /s My /tr $Timestamper /td SHA256 /v "$($file.FullName)"
                }
            }
        }
    } catch {
        Write-ErrorAndExit "signtool failed executing: $_"
    }
    if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "signtool failed signing $($file.FullName) (exit $LASTEXITCODE)" }
    Write-Host "Signed $($file.FullName) with $signtool"
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
        # Use certificate common name derived above for nuget.exe
        $subj = $NugetCertSubjectName
        if ([string]::IsNullOrEmpty($subj)) { $subj = $CertSubjectName }
        if (-not [string]::IsNullOrEmpty($CertStoreLocation)) {
            & "$NugetExePath" sign "$($pkg.FullName)" -CertificateSubjectName "$subj" -CertificateStoreLocation $CertStoreLocation -CertificateStoreName My -Timestamper $Timestamper -Verbosity detailed
        } else {
            & "$NugetExePath" sign "$($pkg.FullName)" -CertificateSubjectName "$subj" -CertificateStoreLocation CurrentUser -CertificateStoreName My -Timestamper $Timestamper -Verbosity detailed
        }
    }
    $nugetExit = $LASTEXITCODE
    if ($nugetExit -ne 0) {
        Write-Warning "nuget.exe sign failed (exit $nugetExit). Attempting fallback: 'dotnet nuget sign'..."
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnetCmd) { Write-ErrorAndExit "dotnet not found; cannot perform fallback signing." }

        if ($usePfx) {
            & "$($dotnetCmd.Source)" nuget sign "$($pkg.FullName)" --certificate-path "$CertPfxPath" --certificate-password "$CertPfxPassword" --timestamper $Timestamper --verbosity detailed
        } else {
            $subj = $NugetCertSubjectName
            if ([string]::IsNullOrEmpty($subj)) { $subj = $CertSubjectName }
            if (-not [string]::IsNullOrEmpty($CertStoreLocation)) {
                & "$($dotnetCmd.Source)" nuget sign "$($pkg.FullName)" --certificate-subject-name "$subj" --certificate-store-location $CertStoreLocation --certificate-store-name My --timestamper $Timestamper --verbosity detailed
            } else {
                & "$($dotnetCmd.Source)" nuget sign "$($pkg.FullName)" --certificate-subject-name "$subj" --certificate-store-location CurrentUser --certificate-store-name My --timestamper $Timestamper --verbosity detailed
            }
        }
        if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "dotnet nuget sign fallback failed for $($pkg.FullName) (exit $LASTEXITCODE)" }
    }

    Write-Host "Verifying package $($pkg.FullName)"
    & "$NugetExePath" verify -All "$($pkg.FullName)"
    if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "nuget.exe verify failed for $($pkg.FullName) (exit $LASTEXITCODE)" }
}

Write-Host "All packages signed and verified. Packages located in: $OutputDir"
