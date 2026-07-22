# Builds a signed, installable Android package (APK) of YamuraView.
#
# Output: YamuraView\bin\Release\net10.0-android\publish\com.yamuraelectronics.yamuraview-Signed.apk
# Copy the APK to the device and open it to install (sideload).
#
# Does NOT bump the build number - it publishes whatever version buildnumber.txt currently
# holds, so a Windows release (publish-windows.ps1, which does bump) followed by this
# produces matching version numbers on both platforms. The Android versionCode is tied to
# the same build number, so each new release installs as an update over the previous one.
#
# Signing uses yamuraview.keystore next to this script (create it once with keytool).
# The store/key passwords come from environment variables so no secret lives in the repo:
#   $env:YAMURAVIEW_KEYSTORE_PASS  - keystore (store) password
#   $env:YAMURAVIEW_KEY_PASS       - key password (defaults to the store password)
# KEEP THE KEYSTORE FILE AND ITS PASSWORDS SAFE and OUT OF GIT: Android only accepts
# updates signed with the same key - if it's lost, existing installs must be uninstalled
# before a new build can be installed.

$ErrorActionPreference = "Stop"

$keystore = Join-Path $PSScriptRoot "yamuraview.keystore"
if (-not (Test-Path $keystore)) {
    Write-Error "Signing keystore not found: $keystore"
}

$storePass = $env:YAMURAVIEW_KEYSTORE_PASS
if (-not $storePass) {
    Write-Error "Set `$env:YAMURAVIEW_KEYSTORE_PASS (the keystore password) before publishing."
}
$keyPass = $env:YAMURAVIEW_KEY_PASS
if (-not $keyPass) { $keyPass = $storePass }

$project = Join-Path $PSScriptRoot "YamuraView\YamuraView.csproj"

# clear previous Release outputs so packaging never reuses stale version metadata
# (the Windows MSIX packaging demonstrably did; cheap insurance here too)
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue `
    (Join-Path $PSScriptRoot "YamuraView\bin\Release\net10.0-android"), `
    (Join-Path $PSScriptRoot "YamuraView\obj\Release\net10.0-android")

dotnet publish $project -f net10.0-android -c Release `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore=$keystore `
    -p:AndroidSigningKeyAlias=yamuraview `
    -p:AndroidSigningStorePass=$storePass `
    -p:AndroidSigningKeyPass=$keyPass
exit $LASTEXITCODE
