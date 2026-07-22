# Builds a signed, installable Windows package (MSIX) of YamuraView.
#
# Output goes to:
#   YamuraView\bin\Release\net10.0-windows10.0.19041.0\win-x64\AppPackages\YamuraView_<version>_Test\
# containing the .msix, the signing certificate (.cer), and an Install.ps1 that
# installs both. Copy that whole folder to the target machine and run Install.ps1.
#
# Each run bumps the build number (buildnumber.txt) first, so a new installer always
# has a higher version than the last and installs cleanly over it.
#
# Signing uses a self-signed certificate whose subject must match the Publisher in
# Platforms\Windows\Package.appxmanifest (CN=YamuraView); it's created in the current
# user's personal certificate store on first run and reused afterward.

$ErrorActionPreference = "Stop"

$subject = "CN=YamuraView"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName "YamuraView MSIX signing" -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(5)
    Write-Host "Created signing certificate $($cert.Thumbprint)"
}

$project = Join-Path $PSScriptRoot "YamuraView\YamuraView.csproj"

# bump the build number (same target a full Release build runs)
dotnet msbuild $project -t:BumpBuildNumberForRelease -p:Configuration=Release -v:m -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# clear previous Release outputs: the MSIX packaging step reuses its generated package
# manifest when only the version properties changed, which would ship the old version
# number (observed with 1.0.0 -> 1.0.1)
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue `
    (Join-Path $PSScriptRoot "YamuraView\bin\Release"), `
    (Join-Path $PSScriptRoot "YamuraView\obj\Release"), `
    (Join-Path $PSScriptRoot "YamuraView.Core\bin\Release"), `
    (Join-Path $PSScriptRoot "YamuraView.Core\obj\Release")

dotnet publish $project -f net10.0-windows10.0.19041.0 -c Release `
    -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$($cert.Thumbprint)
exit $LASTEXITCODE
