# Bumps the build number ONCE and builds BOTH installers at that same version:
#   1. Windows MSIX  - via publish-windows.ps1 (this is the step that bumps buildnumber.txt)
#   2. Android APK   - via publish-android.ps1 (no bump; reuses the number Windows just wrote)
# Result: a matching 1.0.<N> on both platforms.
#
# Output:
#   Windows: YamuraView\bin\Release\net10.0-windows10.0.19041.0\win-x64\AppPackages\YamuraView_<ver>_Test\
#            (copy the folder to the target machine and run Install.ps1)
#   Android: YamuraView\bin\Release\net10.0-android\publish\com.yamuraelectronics.yamuraview-Signed.apk
#            (sideload it)
#
# Android signing needs the keystore password in the environment (same as publish-android.ps1):
#   $env:YAMURAVIEW_KEYSTORE_PASS  - keystore (store) password   [required]
#   $env:YAMURAVIEW_KEY_PASS       - key password (defaults to the store password)
# These are checked UP FRONT so a missing password fails before Windows bumps + builds.
#
# -Commit: after both installers build, commit the bumped YamuraView\buildnumber.txt
#          ("Bump build number to <N> for <ver> release"). Does not push.
#
# The two sub-scripts each end with `exit`, which would terminate this orchestrator if they
# were dot-sourced or called with `&`; they're run as child processes so only their own exit
# code comes back (checked below).

param(
    [switch]$Commit
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$winScript = Join-Path $root "publish-windows.ps1"
$androidScript = Join-Path $root "publish-android.ps1"
$keystore = Join-Path $root "yamuraview.keystore"

# ---- fail fast on Android prerequisites, before we bump + build anything ----
if (-not (Test-Path $winScript)) { Write-Error "Missing $winScript" }
if (-not (Test-Path $androidScript)) { Write-Error "Missing $androidScript" }
if (-not (Test-Path $keystore)) { Write-Error "Signing keystore not found: $keystore" }
if (-not $env:YAMURAVIEW_KEYSTORE_PASS) {
    Write-Error "Set `$env:YAMURAVIEW_KEYSTORE_PASS (the keystore password) before running - both installers are built in one pass, so this is checked first."
}

function Invoke-Publish {
    param([string]$Script, [string]$Label)
    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan
    # child process: the sub-script's own `exit` stays inside it; we read its exit code
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$Label failed (exit code $LASTEXITCODE). Stopping."
    }
}

# Windows first: bumps buildnumber.txt, then builds and signs the MSIX.
Invoke-Publish -Script $winScript -Label "Windows (MSIX) - bumps the build number"

# Android next: no bump, uses the number Windows just wrote, so the versions match.
Invoke-Publish -Script $androidScript -Label "Android (APK)"

# ---- report ----
$version = "1.0." + (Get-Content (Join-Path $root "YamuraView\buildnumber.txt")).Trim()
$appPkgDir = Join-Path $root "YamuraView\bin\Release\net10.0-windows10.0.19041.0\win-x64\AppPackages"
$msixFolder = Get-ChildItem $appPkgDir -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
$apk = Join-Path $root "YamuraView\bin\Release\net10.0-android\publish\com.yamuraelectronics.yamuraview-Signed.apk"

Write-Host ""
Write-Host "Both installers built at $version." -ForegroundColor Green
if ($msixFolder) { Write-Host "  Windows: $($msixFolder.FullName)  (run Install.ps1 inside it)" }
Write-Host "  Android: $apk"

# ---- optional: commit the bumped build number ----
if ($Commit) {
    $buildNumber = (Get-Content (Join-Path $root "YamuraView\buildnumber.txt")).Trim()
    Write-Host ""
    # only commit if buildnumber.txt actually differs from HEAD (it will after a bump)
    git -C $root diff --quiet -- YamuraView/buildnumber.txt
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Nothing to commit - buildnumber.txt is unchanged from HEAD." -ForegroundColor Yellow
    }
    else {
        git -C $root add YamuraView/buildnumber.txt
        if ($LASTEXITCODE -ne 0) { Write-Error "git add failed (exit $LASTEXITCODE)." }
        git -C $root commit -m "Bump build number to $buildNumber for $version release"
        if ($LASTEXITCODE -ne 0) { Write-Error "git commit failed (exit $LASTEXITCODE)." }
        Write-Host "Committed the build-number bump ($version). Push with: git push" -ForegroundColor Green
    }
}
