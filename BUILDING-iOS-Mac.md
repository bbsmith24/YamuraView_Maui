# Building YamuraView for iOS and Mac (Mac Catalyst)

YamuraView is a .NET 10 MAUI app. The Apple targets declared in
[YamuraView.csproj](YamuraView/YamuraView.csproj) are:

| Target | TFM | Minimum OS |
|---|---|---|
| iOS (iPhone/iPad) | `net10.0-ios` | iOS 15.0 |
| Mac (Mac Catalyst) | `net10.0-maccatalyst` | macOS 12 (Catalyst 15.0) |

Both targets are excluded automatically when building on Linux; they are
*evaluated* on Windows, but **compiling and running them requires a Mac** —
Apple's toolchain (Xcode) only runs on macOS. There are two workflows:

1. **Build directly on a Mac** (recommended, simplest) — clone the repo on the
   Mac and use the `dotnet` CLI or VS Code.
2. **Build from Windows paired to a Mac** — Visual Studio 2022's
   *Pair to Mac* remote build for iOS. Mac Catalyst cannot be built from
   Windows at all, even when paired.

> Visual Studio for Mac was retired in 2024. On macOS use the `dotnet` CLI
> (everything in this document) or VS Code with the ".NET MAUI" extension.

---

## 1. One-time Mac setup

1. **Xcode** — install from the App Store, launch it once to accept the
   license, then install the command-line tools and platforms:

   ```bash
   sudo xcode-select --switch /Applications/Xcode.app
   xcodebuild -runFirstLaunch
   ```

   Each .NET release pins a specific Xcode version. Check the version your
   installed iOS workload expects with `dotnet workload list` and the
   [.NET MAUI release notes](https://github.com/dotnet/maui/releases) —
   for .NET 10 this is Xcode 26.x. A too-old Xcode fails the build with a
   clear "requires Xcode N" error.

2. **.NET 10 SDK** — install from <https://dotnet.microsoft.com/download>,
   then verify with `dotnet --version` (must report 10.0.x).

3. **MAUI workloads**:

   ```bash
   sudo dotnet workload install maui
   ```

   (Installs the iOS, Mac Catalyst, and Android workloads. After a .NET SDK
   update, re-run `sudo dotnet workload update`.)

4. **Apple developer account** — needed to run on a physical iPhone/iPad and
   for any distribution. Free accounts can deploy to their own devices;
   App Store / TestFlight / notarized Mac distribution requires the paid
   Apple Developer Program. Sign into Xcode (Settings → Accounts) with the
   Apple ID so Xcode can create/download certificates and provisioning
   profiles.

---

## 2. Debug builds on the Mac

All commands run from the repo root.

### Mac Catalyst (runs natively on the Mac)

```bash
dotnet build YamuraView/YamuraView.csproj -f net10.0-maccatalyst -t:Run
```

This builds and launches the app as a Mac desktop app. No signing setup is
needed for local debug runs.

### iOS Simulator

```bash
dotnet build YamuraView/YamuraView.csproj -f net10.0-ios -t:Run
```

By default this boots the default iOS Simulator. To pick a specific
simulator, list them and pass the UDID:

```bash
xcrun simctl list devices available
dotnet build YamuraView/YamuraView.csproj -f net10.0-ios -t:Run \
    -p:_DeviceName=:v2:udid=<SIMULATOR-UDID>
```

Simulator builds are not code-signed, so they need no certificates.

### iOS physical device

Device builds must be signed. The easiest path is to let Xcode manage
signing once, then reference the identity/profile from the CLI:

1. In Xcode create (or open) any project with the bundle ID
   `com.yamuraelectronics.yamuraview` (the `ApplicationId` from the csproj),
   enable *Automatically manage signing*, and deploy once to the device.
   This registers the device and creates a development provisioning profile.
2. Build and deploy from the CLI:

   ```bash
   dotnet build YamuraView/YamuraView.csproj -f net10.0-ios -t:Run \
       -p:RuntimeIdentifier=ios-arm64 \
       -p:CodesignKey="Apple Development: Your Name (TEAMID)" \
       -p:CodesignProvision="<profile name>"
   ```

   List installed signing identities with
   `security find-identity -v -p codesigning`.

The device must be in Developer Mode (Settings → Privacy & Security →
Developer Mode) and trusted from the Mac.

### VS Code one-click tasks

The repo includes [.vscode/tasks.json](.vscode/tasks.json) with ready-to-run build,
run, and publish tasks so you don't have to retype the `dotnet` commands. To use them:

- Install the **.NET MAUI** extension (`ms-dotnettools.dotnet-maui`) in VS Code — it
  pulls in the **C# Dev Kit** and **C#** extensions automatically.
- Open the **repository root** folder in VS Code (`code .`) so the task paths resolve.

Run a task from **Terminal → Run Task…** (or the Command Palette → *Tasks: Run Task*);
build errors appear in the **Problems** panel.

| Task | Action |
|---|---|
| 🍎 Build Mac Catalyst | Debug build. Default build task → `Cmd+Shift+B`. |
| 🍎 Run Mac Catalyst | Debug build and launch on the Mac. |
| 📱 Build iOS (Simulator) | Debug build for iOS. |
| 🍎 Publish Mac Catalyst (.app, universal) | Release universal `.app` (see section 4). |
| 📱 Publish iOS (.ipa, signed) | Release signed `.ipa` — prompts for signing identity + profile (see section 4). |

For **F5 debugging**, and for running on the **iOS Simulator or a device**, use the
.NET MAUI extension's target picker in the status bar instead of a task — it supplies
the simulator/device identifier automatically.

To bind the other tasks to single keystrokes, add entries to VS Code's
keybindings.json (Command Palette → *Preferences: Open Keyboard Shortcuts (JSON)*):

```json
[
  { "key": "cmd+shift+i", "command": "workbench.action.tasks.runTask", "args": "📱 Build iOS (Simulator)" },
  { "key": "cmd+shift+r", "command": "workbench.action.tasks.runTask", "args": "🍎 Run Mac Catalyst" }
]
```

> The publish tasks map to the `dotnet publish` commands in
> [section 4](#4-release--distribution-builds). To skip the iOS prompts, replace the
> `${input:...}` placeholders in `.vscode/tasks.json` with your literal signing values.

---

## 3. Building from Windows (Pair to Mac)

Visual Studio 2022 (17.14+ for .NET 10) can build and debug the **iOS**
target from this Windows checkout by remoting the native build to a Mac:

1. On the Mac: enable *Remote Login* (System Settings → General → Sharing),
   and complete the one-time setup from section 1 (Xcode, .NET SDK, workloads).
2. In Visual Studio: open [YamuraView.sln](YamuraView/YamuraView.sln), select
   the `net10.0-ios` target, then *Tools → iOS → Pair to Mac* and connect.
3. Choose an iOS Simulator (renders in the remoted simulator window) or a
   USB/Wi-Fi-connected device and run.

Limitations:

- **Mac Catalyst builds are not supported from Windows** — build that target
  on the Mac itself.
- Signing for device deployment still happens on the paired Mac, so the
  certificates/profiles from section 2 must exist there.

---

## 4. Release / distribution builds

### Version numbering (project-specific — read this first)

The version is assembled in the csproj from `VersionMajor`.`VersionMinor`
plus a build number stored in `YamuraView/buildnumber.txt`:

- A **full Release build** (no `-f` framework filter) bumps
  `buildnumber.txt` once at the start, and every platform in that build gets
  the same number.
- A **per-platform publish with `-f`** skips the bump and reuses the current
  number. So to ship a coordinated release across platforms: run one full
  Release build first, then publish each platform with `-f`.
- `VersionStatus` (`beta` currently) is appended to the informational
  version; clear it in the csproj for a final release.
- On Apple platforms `ApplicationDisplayVersion` becomes `CFBundleShortVersionString`
  and `ApplicationVersion` (the build number) becomes `CFBundleVersion`.
  App Store Connect requires `CFBundleVersion` to increase with every upload —
  the auto-bump takes care of this as long as releases go through full
  Release builds.

Commit the updated `buildnumber.txt` after a release build so the next
release continues the sequence.

### iOS — App Store / TestFlight (.ipa)

Prereqs (one-time, in App Store Connect / Xcode):

- An **App Store distribution certificate** and an **App Store provisioning
  profile** for `com.yamuraelectronics.yamuraview`.
- An app record in App Store Connect with that bundle ID.

Publish:

```bash
dotnet publish YamuraView/YamuraView.csproj -f net10.0-ios -c Release \
    -p:RuntimeIdentifier=ios-arm64 \
    -p:CodesignKey="Apple Distribution: Yamura Electronics (TEAMID)" \
    -p:CodesignProvision="<App Store profile name>"
```

The `.ipa` lands under
`YamuraView/bin/Release/net10.0-ios/ios-arm64/publish/`. Upload it with
Xcode's *Transporter* app or:

```bash
xcrun altool --upload-app --type ios -f YamuraView.ipa \
    --apiKey <key-id> --apiIssuer <issuer-id>
```

For ad-hoc distribution to registered test devices, use an *Ad Hoc*
provisioning profile instead of the App Store one.

### Mac Catalyst — Mac App Store (.pkg)

The required entitlements (App Sandbox + outgoing network client) are
already configured in
[Entitlements.plist](YamuraView/Platforms/MacCatalyst/Entitlements.plist).

Prereqs: **Apple Distribution** + **Mac Installer Distribution**
certificates, and a Mac App Store provisioning profile for the bundle ID.

```bash
dotnet publish YamuraView/YamuraView.csproj -f net10.0-maccatalyst -c Release \
    -p:MtouchLink=SdkOnly \
    -p:CreatePackage=true \
    -p:EnableCodeSigning=true \
    -p:EnablePackageSigning=true \
    -p:CodesignKey="Apple Distribution: Yamura Electronics (TEAMID)" \
    -p:CodesignProvision="<Mac App Store profile name>" \
    -p:CodesignEntitlements="Platforms/MacCatalyst/Entitlements.plist" \
    -p:PackageSigningKey="3rd Party Mac Developer Installer: Yamura Electronics (TEAMID)"
```

The signed `.pkg` lands under
`YamuraView/bin/Release/net10.0-maccatalyst/publish/` — upload it with
Transporter.

### Mac Catalyst — direct distribution outside the App Store

Sign with a **Developer ID** certificate instead, then notarize:

```bash
dotnet publish YamuraView/YamuraView.csproj -f net10.0-maccatalyst -c Release \
    -p:CreatePackage=true \
    -p:EnableCodeSigning=true \
    -p:CodesignKey="Developer ID Application: Yamura Electronics (TEAMID)" \
    -p:CodesignProvision="<Developer ID profile name>" \
    -p:CodesignEntitlements="Platforms/MacCatalyst/Entitlements.plist" \
    -p:UseHardenedRuntime=true

xcrun notarytool submit <path-to-pkg-or-zip> --wait \
    --apple-id <apple-id> --team-id <TEAMID> --password <app-specific-password>
xcrun stapler staple YamuraView.app
```

---

## 5. Troubleshooting

- **"Xcode N.N or later is required"** — update Xcode, then re-run
  `xcodebuild -runFirstLaunch` and `sudo dotnet workload update`.
- **Workload mismatch after an SDK update** — `sudo dotnet workload repair`.
- **Signing errors ("no valid identity/profile")** — open Xcode →
  Settings → Accounts → Download Manual Profiles, and confirm the profile's
  bundle ID exactly matches `com.yamuraelectronics.yamuraview`. The
  `ApplicationId` must never change once the app has been distributed.
- **Simulator won't boot / stale state** — `xcrun simctl shutdown all` then
  `xcrun simctl erase all` (erases all simulator data).
- **Clean rebuild** — delete the `bin/` and `obj/` folders under both
  `YamuraView/` and `YamuraView.Core/`; `dotnet clean` alone often isn't
  enough for MAUI multi-targeting issues.
- **`codesign` fails with "resource fork, Finder information, or similar
  detritus not allowed"** — the checkout is inside an iCloud-synced folder.
  With *Desktop & Documents Folders* sync enabled, `fileproviderd` stamps
  `com.apple.FinderInfo` (the bundle bit) on every `.app` it sees, and
  `codesign` refuses to sign a bundle carrying it. `xattr -cr` does **not**
  fix it — the daemon re-adds the attribute within seconds, so the next
  build fails identically. Keep the repo outside `~/Documents` and
  `~/Desktop`; this one lives in `~/dev/YamuraView_Maui` for that reason.
- **"Open" does nothing on Mac Catalyst** — the app is missing the
  `com.apple.security.files.user-selected.read-write` entitlement. Without it
  `UIDocumentPickerViewController` presents, but its view stays hidden, no
  panel window is ever created, and the picker task never completes, so the
  button looks dead with no error anywhere. Every other button still works,
  which makes it look like an Open-specific bug rather than a signing one.
  See *Entitlements and the file picker* below.

### Entitlements and the file picker (Mac Catalyst)

The Open button needs `com.apple.security.files.user-selected.read-write`.
This was verified by bisecting the entitlements on an otherwise identical
build and watching the window server for a panel window:

| Entitlements on the bundle | Open panel appears? |
| -------------------------- | ------------------- |
| none (MAUI's Debug default) | no |
| `app-sandbox` + `network.client` | no |
| `app-sandbox` + `network.client` + `user-selected.read-write` | yes |
| `user-selected.read-write` alone | yes |

App Sandbox is *not* required for the picker — only the user-selected-files
entitlement is. Two files cover the two cases:

- [Entitlements.Unsandboxed.plist](YamuraView/Platforms/MacCatalyst/Entitlements.Unsandboxed.plist) —
  user-selected-files only, no sandbox. The `.csproj` makes this the default
  for **every** Mac Catalyst build, because MAUI otherwise signs them ad-hoc
  with no entitlements at all. This covers both local Debug runs and the
  unsigned universal `.app`, which had the same dead Open button. The sandbox
  is deliberately left off so the config file and autoload folder keep their
  arbitrary-path access.
- [Entitlements.plist](YamuraView/Platforms/MacCatalyst/Entitlements.plist) —
  App Sandbox on (required by the Mac App Store) plus the network and
  user-selected-files entitlements. The App Store `.pkg` and Developer ID
  commands above pass this explicitly with
  `-p:CodesignEntitlements=...`; a global property set on the command line
  overrides the `.csproj` default, so those builds still get the sandbox.

Because only the App Store / Developer ID builds are sandboxed, the config
file path and autoload folder need re-checking before an App Store submission
— a sandboxed app can only reach paths the user picked, and both of those are
arbitrary paths remembered across runs.
