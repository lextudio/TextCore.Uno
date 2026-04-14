LeXtudio.UI.Text.Core — Packaging and Signing
---------------------------------------------

This repository ships the `LeXtudio.UI.Text.Core` library. To produce a signed
NuGet package (Windows-only), a script is provided that uses `signtool.exe` to
sign binaries and `nuget.exe` to sign the produced `.nupkg` files.

1. Requirements
  - .NET SDK (`dotnet`).
  - Windows with Windows SDK / Visual Studio installed (for `signtool.exe`).
  - A code-signing certificate available either as a PFX (`CERT_PFX_PATH` + `CERT_PFX_PASSWORD`) or
    installed in `Cert:\CurrentUser\My` (script can auto-discover if not provided).

2. Quick command (Windows)

  ```powershell
  cd external\coretext
  .\dist.all.bat Release
  ```

3. What `dist.all.bat` / `sign-and-pack.ps1` do
  - `dotnet build` the project outputs.
  - Use `signtool.exe` to sign compiled `.dll` and `.exe` outputs.
  - `dotnet pack --no-build` to create `.nupkg` files from the already-signed outputs.
  - Use `nuget.exe sign` to sign the `.nupkg` files and `nuget.exe verify` to verify signatures.

4. Environment variables supported
  - `CERT_PFX_PATH` and `CERT_PFX_PASSWORD` — prefer a PFX in CI.
  - `CERT_SUBJECT_NAME` — use a certificate present in `Cert:\CurrentUser\My` by subject name.

5. Notes
  - The script will download `nuget.exe` into `tools\` if not already present.
  - `signtool.exe` must be available (either in PATH or installed with the Windows SDK).
  - Publishing to NuGet.org is intentionally separate — use `dist.publish2nugetdotorg.bat` to push packages.
