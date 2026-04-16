param(
    [string]$PackagesDir = ".\\dist",
    [string]$CertPfxPath = $env:CERT_PFX_PATH,
    [string]$CertPfxPassword = $env:CERT_PFX_PASSWORD,
    [string]$CertSubjectName = $env:CERT_SUBJECT_NAME,
    [string]$NugetExePath = "",
    [switch]$PreferDotnetSign = $false
)

function Write-ErrorAndExit($msg, $code = 1) {
    Write-Error $msg
    exit $code
}

# Attempts to find a suitable code-signing certificate in CurrentUser\My
function Find-CodeSigningCert {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My","CurrentUser")
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    try {
        $now = Get-Date
        $candidates = @()
        foreach ($cert in $store.Certificates) {
            if (-not $cert.HasPrivateKey) { continue }
            if ($cert.NotBefore -gt $now -or $cert.NotAfter -lt $now) { continue }
            $ekuExt = $cert.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }
            if ($ekuExt) {
                $oids = @()
                foreach ($oid in $ekuExt.EnhancedKeyUsages) { $oids += $oid.Value }
                if ($oids -contains '1.3.6.1.5.5.7.3.3') { $candidates += $cert; continue }
            }
        }
        if ($candidates.Count -gt 0) {
            return ($candidates | Sort-Object NotAfter -Descending | Select-Object -First 1)
        }
        # Fallback: any valid certificate with private key
        $any = $store.Certificates | Where-Object { $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -ge $now } | Sort-Object NotAfter -Descending | Select-Object -First 1
        return $any
    } finally {
        $store.Close()
    }
}

Write-Host "Signing packages in: $PackagesDir"

if (-not (Test-Path $PackagesDir)) { Write-ErrorAndExit "PackagesDir not found: $PackagesDir" }

# Locate nuget.exe: prefer tools\nuget.exe, otherwise use nuget on PATH
# If PreferDotnetSign is set, skip preferring nuget.exe for signing and use dotnet instead
if (-not $PreferDotnetSign -and [string]::IsNullOrEmpty($NugetExePath)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $localNuget = Join-Path $scriptRoot 'tools\nuget.exe'
    if (Test-Path $localNuget) { $NugetExePath = $localNuget }
    else {
        $nugetCmd = Get-Command nuget -ErrorAction SilentlyContinue
        if ($nugetCmd) { $NugetExePath = $nugetCmd.Source }
    }
}

if (-not [string]::IsNullOrEmpty($NugetExePath)) { Write-Host "Using NuGet: $NugetExePath" }
else { Write-Warning "nuget.exe not found. Will attempt 'dotnet nuget sign' fallback." }

$nupkgs = Get-ChildItem -Path $PackagesDir -Filter '*.nupkg' -File
if ($nupkgs.Count -eq 0) { Write-ErrorAndExit "No .nupkg files found in $PackagesDir" }

foreach ($pkg in $nupkgs) {
    Write-Host "Signing package $($pkg.FullName)"

    if (-not [string]::IsNullOrEmpty($NugetExePath)) {
        if (-not [string]::IsNullOrEmpty($CertPfxPath)) {
            & "$NugetExePath" sign "$($pkg.FullName)" -CertificatePath "$CertPfxPath" -CertificatePassword "$CertPfxPassword" -Timestamper http://timestamp.digicert.com -Verbosity detailed
        } else {
            if (-not [string]::IsNullOrEmpty($CertSubjectName)) {
                & "$NugetExePath" sign "$($pkg.FullName)" -CertificateSubjectName "$CertSubjectName" -CertificateStoreLocation CurrentUser -CertificateStoreName My -Timestamper http://timestamp.digicert.com -Verbosity detailed
            } else {
                Write-ErrorAndExit "No certificate info provided. Set CERT_PFX_PATH/CERT_PFX_PASSWORD or CERT_SUBJECT_NAME."
            }
        }

        if ($LASTEXITCODE -ne 0) { Write-Warning "nuget.exe sign failed (exit $LASTEXITCODE). Falling back to 'dotnet nuget sign'." }
        else { Write-Host "Signed with nuget.exe"; continue }
    }

    # Fallback to dotnet nuget sign
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) { Write-ErrorAndExit "dotnet not found; cannot sign package." }

    # If no PFX path or subject name provided, attempt to auto-find a code-signing cert
    if ([string]::IsNullOrEmpty($CertPfxPath) -and [string]::IsNullOrEmpty($CertSubjectName)) {
        $autoCert = Find-CodeSigningCert
        if ($autoCert) {
            $autoSimpleName = $autoCert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
            Write-Host "Auto-selected code signing certificate: $($autoSimpleName) (thumbprint $($autoCert.Thumbprint))"
            $CertSubjectName = $autoSimpleName
        }
    }

    if (-not [string]::IsNullOrEmpty($CertPfxPath)) {
        & "$($dotnetCmd.Source)" nuget sign "$($pkg.FullName)" --certificate-path "$CertPfxPath" --certificate-password "$CertPfxPassword" --timestamper http://timestamp.digicert.com --verbosity detailed
    } else {
        if (-not [string]::IsNullOrEmpty($CertSubjectName)) {
            & "$($dotnetCmd.Source)" nuget sign "$($pkg.FullName)" --certificate-subject-name "$CertSubjectName" --certificate-store-location CurrentUser --certificate-store-name My --timestamper http://timestamp.digicert.com --verbosity detailed
        } else {
            Write-ErrorAndExit "No certificate info provided for dotnet nuget sign fallback."
        }
    }

    if ($LASTEXITCODE -ne 0) { Write-ErrorAndExit "Signing failed for $($pkg.FullName) (exit $LASTEXITCODE)" }
    Write-Host "Signed $($pkg.FullName)"

    # Verify
    if (-not [string]::IsNullOrEmpty($NugetExePath)) {
        & "$NugetExePath" verify -All "$($pkg.FullName)"
        if ($LASTEXITCODE -ne 0) { Write-Warning "nuget.exe verify failed for $($pkg.FullName)" }
    }
}

Write-Host "All packages signed (or attempted)."
