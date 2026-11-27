#!/usr/bin/env bash
#
# publish-apple.sh - pull the latest YamuraView from GitHub, then build and install it
# on this Mac (signed + notarized Mac Catalyst .pkg) and on a USB-tethered iPhone/iPad.
#
# Run from anywhere on the Mac:
#     ./publish-apple.sh                 # pull, Mac release + install, iOS build + install
#     ./publish-apple.sh --no-notarize   # skip Apple notarization (faster local install)
#     ./publish-apple.sh --skip-mac      # iOS device only
#     ./publish-apple.sh --skip-ios      # Mac only
#     ./publish-apple.sh --skip-pull     # build what's checked out, don't git pull
#     ./publish-apple.sh --udid <UDID>   # pick the iOS device instead of auto-detecting
#
# Like publish-android.ps1, this does NOT bump buildnumber.txt - it builds whatever
# version the last Windows release set (pulled from GitHub), so all platforms match.
# See BUILDING-iOS-Mac.md for the one-time Mac setup and what each signing value is.
#
# Signing identities can be overridden with environment variables:
#   MAC_APP_IDENTITY, MAC_INSTALLER_IDENTITY, IOS_IDENTITY, IOS_PROFILE, NOTARY_PROFILE

set -euo pipefail

MAC_APP_IDENTITY="${MAC_APP_IDENTITY:-Developer ID Application: Brian Smith (W52E539DAG)}"
MAC_INSTALLER_IDENTITY="${MAC_INSTALLER_IDENTITY:-Developer ID Installer: Brian Smith (W52E539DAG)}"
IOS_IDENTITY="${IOS_IDENTITY:-Apple Development: Brian Smith (YESM22X7CU)}"
IOS_PROFILE="${IOS_PROFILE:-iOS Team Provisioning Profile: com.yamuraelectronics.yamuraview}"
NOTARY_PROFILE="${NOTARY_PROFILE:-YamuraView-Notary}"
BUNDLE_ID="com.yamuraelectronics.yamuraview"

DO_PULL=1
DO_MAC=1
DO_IOS=1
DO_NOTARIZE=1
UDID=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-pull)   DO_PULL=0 ;;
        --skip-mac)    DO_MAC=0 ;;
        --skip-ios)    DO_IOS=0 ;;
        --no-notarize) DO_NOTARIZE=0 ;;
        --udid)        UDID="${2:?--udid needs a value}"; shift ;;
        -h|--help)     sed -n '2,20p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
    esac
    shift
done

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO"
PROJECT="YamuraView/YamuraView.csproj"

step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
fail() { printf '\n\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- pull
if [[ $DO_PULL -eq 1 ]]; then
    step "Pulling latest from GitHub"
    if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
        fail "Local changes in $REPO - commit/stash them or use --skip-pull"
    fi
    git checkout master
    git pull --ff-only origin master
fi

BUILD_NUMBER="$(tr -d '[:space:]' < YamuraView/buildnumber.txt)"
step "Building YamuraView build $BUILD_NUMBER ($(git log -1 --format='%h %s'))"

# ---------------------------------------------------------------- Mac Catalyst
if [[ $DO_MAC -eq 1 ]]; then
    step "Mac Catalyst: release build + signed .pkg (universal)"
    rm -f YamuraView/bin/Release/net10.0-maccatalyst/publish/YamuraView-*.pkg
    dotnet publish "$PROJECT" -f net10.0-maccatalyst -c Release \
        -p:CreatePackage=true \
        -p:EnableCodeSigning=true \
        -p:EnablePackageSigning=true \
        -p:CodesignKey="$MAC_APP_IDENTITY" \
        -p:PackageSigningKey="$MAC_INSTALLER_IDENTITY" \
        -p:CodesignEntitlements="Platforms/MacCatalyst/Entitlements.Unsandboxed.plist" \
        -p:UseHardenedRuntime=true

    PKG="$(ls -t YamuraView/bin/Release/net10.0-maccatalyst/publish/YamuraView-*.pkg 2>/dev/null | head -n 1 || true)"
    [[ -n "$PKG" ]] || fail "No .pkg produced under YamuraView/bin/Release/net10.0-maccatalyst/publish/"
    pkgutil --check-signature "$PKG" | head -n 3

    if [[ $DO_NOTARIZE -eq 1 ]]; then
        step "Notarizing $(basename "$PKG") (keychain profile '$NOTARY_PROFILE') - usually a few minutes"
        xcrun notarytool submit "$PKG" --keychain-profile "$NOTARY_PROFILE" --wait
        xcrun stapler staple "$PKG"
        spctl -a -vvv -t install "$PKG" || fail "Gatekeeper rejected the notarized package"
    fi

    step "Installing $(basename "$PKG") on this Mac (asks for your Mac password)"
    osascript -e 'quit app "YamuraView"' >/dev/null 2>&1 || true
    sudo installer -pkg "$PKG" -target /
    echo "Installed: /Applications/YamuraView.app"
fi

# ---------------------------------------------------------------- iOS device
if [[ $DO_IOS -eq 1 ]]; then
    if [[ -z "$UDID" ]]; then
        step "Looking for a tethered iPhone/iPad"
        JSON="$(mktemp -t devices).json"
        xcrun devicectl list devices --json-output "$JSON" >/dev/null 2>&1 || true
        UDID="$(/usr/bin/python3 - "$JSON" <<'PY' || true
import json, sys
try:
    devices = json.load(open(sys.argv[1]))["result"]["devices"]
except Exception:
    sys.exit(0)
def pick(require_wired):
    for d in devices:
        hw = d.get("hardwareProperties", {})
        cp = d.get("connectionProperties", {})
        if hw.get("platform") != "iOS" or hw.get("reality") == "virtual":
            continue
        if cp.get("pairingState") not in (None, "paired"):
            continue
        if require_wired and cp.get("transportType") != "wired":
            continue
        if hw.get("udid"):
            return hw["udid"], d.get("deviceProperties", {}).get("name", "?")
    return None
hit = pick(True) or pick(False)
if hit:
    print(hit[0])
    print("Found: %s (%s)" % (hit[1], hit[0]), file=sys.stderr)
PY
)"
        rm -f "$JSON"
        [[ -n "$UDID" ]] || fail "No paired iOS device found - plug it in, unlock it, trust this Mac, or pass --udid (xcrun xctrace list devices)"
    fi

    step "iOS: signed device build (ios-arm64)"
    dotnet build "$PROJECT" -f net10.0-ios -c Debug \
        -p:RuntimeIdentifier=ios-arm64 \
        -p:CodesignKey="$IOS_IDENTITY" \
        -p:CodesignProvision="$IOS_PROFILE"

    APP="YamuraView/bin/Debug/net10.0-ios/ios-arm64/YamuraView.app"
    [[ -d "$APP" ]] || fail "No app bundle at $APP"

    # devicectl, not 'dotnet -t:Run' - mlaunch's device install hangs on iOS 26
    step "Installing on device $UDID"
    xcrun devicectl device install app --device "$UDID" "$APP"
    step "Launching"
    xcrun devicectl device process launch --device "$UDID" "$BUNDLE_ID" \
        || echo "Installed, but launch failed - unlock the device (first run of a new cert: Settings > General > VPN & Device Management > Trust)"
fi

step "Done - build $BUILD_NUMBER"
