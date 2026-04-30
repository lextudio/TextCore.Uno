$P = Get-Location
$dll = Join-Path $P.Path 'src\bin\Release\net9.0-desktop\LeXtudio.UI.Text.Core.dll'
if (!(Test-Path $dll)) { Write-Host "DLL not found: $dll"; exit 2 }
$cmd = Get-Command signtool -ErrorAction SilentlyContinue
if ($cmd) { Write-Host "signtool found on PATH: $($cmd.Source)" ; $sig = $cmd.Source } else {
    $found = Get-ChildItem 'C:\Program Files*' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { Write-Host "Found signtool: $($found.FullName)"; $sig = $found.FullName } else { Write-Host "No signtool found"; exit 3 }
}
Write-Host "Running: $sig sign /v /sha1 9121D6718849B8687CA8B4AC03EA8C9C0F30B07A /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /v $dll"
& $sig sign /v /sha1 9121D6718849B8687CA8B4AC03EA8C9C0F30B07A /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /v $dll
