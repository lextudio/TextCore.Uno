param([string]$Thumb = '9121D6718849B8687CA8B4AC03EA8C9C0F30B07A')

Write-Host "Thumb: $Thumb"

$cert = Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Thumbprint -eq $Thumb }
if (-not $cert) {
    Write-Host 'Not in CurrentUser'
    $cert = Get-ChildItem "Cert:\LocalMachine\My" | Where-Object { $_.Thumbprint -eq $Thumb }
    if (-not $cert) { Write-Host 'Not found in any store'; exit 2 } else { Write-Host 'Found in LocalMachine' }
}

Write-Host "-----Cert Info-----"
$cert | Format-List Subject,Thumbprint,HasPrivateKey,Issuer,NotBefore,NotAfter,FriendlyName,SignatureAlgorithm,PublicKey

Write-Host "-----Key Info via certutil -store -v for store that contains it-----"
if ($cert.PSParentPath -match 'CurrentUser') { certutil -user -store My $Thumb } else { certutil -store My $Thumb }

Write-Host "-----Try GetRSAPrivateKey and sign small blob-----"
try {
    $rsa = $cert.GetRSAPrivateKey()
    if ($rsa -eq $null) { Write-Host 'GetRSAPrivateKey returned null' }
    else {
        $data = [System.Text.Encoding]::UTF8.GetBytes('test')
        $sig = $rsa.SignData($data,[System.Security.Cryptography.HashAlgorithmName]::SHA256,[System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        Write-Host 'RSA.SignData succeeded. Signature length:' $sig.Length
    }
} catch {
    Write-Host 'RSA sign exception:'
    $_ | Format-List *
}

Write-Host "-----Try SignedCms ComputeSignature (simulate NuGet)-----"
try {
    $ci = New-Object System.Security.Cryptography.Pkcs.ContentInfo (,[byte[]](1,2,3))
    $sc = New-Object System.Security.Cryptography.Pkcs.SignedCms $ci, $false
    $signer = New-Object System.Security.Cryptography.Pkcs.CmsSigner $cert
    $sc.ComputeSignature($signer, $false)
    Write-Host 'SignedCms OK'
} catch {
    Write-Host 'SignedCms exception:'
    $_.Exception | Format-List *
}
