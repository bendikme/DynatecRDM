# Installer and releases

## What the MSI does

`DynatecRDM.wxs` builds a **per-user** MSI. It installs into `%LOCALAPPDATA%\Programs\DynatecRDM`
and writes only `HKCU`, so no administrator rights are needed to install, upgrade or uninstall.
That is what lets the in-app updater run the new package without a UAC prompt. It adds a Start
menu shortcut under `DYNATEC` and, only when asked, a `HKCU\...\Run` entry.

There is no wizard: the package has no UI authoring, so double-clicking it shows a progress
window, installs, and starts the app.

## User data is never touched

Connections, credentials, snapshots and logs live in **`%LOCALAPPDATA%\DynatecRDM`**, a different
folder from the installation. The installer has no component, directory or custom action pointing
there, so install, upgrade, repair and uninstall all leave it exactly as it is. Keep it that way:
anything authored under that path would let MSI delete a user's database on uninstall.
`build\publish.ps1` refuses an `-OutDir` inside that folder for the same reason.

## Build a release locally

One-time setup: install the .NET 10 SDK, then

```powershell
dotnet tool install --global wix
wix --acceptEula --version
```

The WiX version is deliberately not pinned; anything from v5 onwards works, which is what the
`<Files>` harvesting element in `DynatecRDM.wxs` needs. The second line accepts the licence WiX 6
and later ask about once per machine (older versions do not have the switch).

Then, from the repository root:

```powershell
.\build\release.ps1 -Version 1.2.3
```

That publishes the app self-contained, packages the MSI, zips a portable copy and writes
checksums into `artifacts\`: `DynatecRDM-1.2.3-x64.msi` (the installer the updater downloads),
`DynatecRDM-1.2.3-win-x64.zip` (unzip and run `DynatecRDM.exe`) and `SHA256SUMS.txt`.
Add `-SkipPublish` to repackage without rebuilding.

Versions are three or four numbers. A `-prerelease` suffix is rejected: MSI compares numbers
only, so `1.2.3-beta` would not upgrade `1.2.3`.

If `wix build` fails, the script retries it once with `-sval`. MSI validation rejects any
component installed under `%LOCALAPPDATA%` (ICE38 and ICE64, which exist for roaming profiles),
and that is exactly what a per-user package does. Only the retry skips validation, so a real
authoring mistake still fails the build - its error simply appears twice.

## Cut a release

```powershell
git tag v1.2.3
git push origin v1.2.3
```

`.github/workflows/release.yml` runs on any `v*` tag - or manually via **Run workflow** with a
version - executes `build\release.ps1` and publishes a GitHub Release with the three files attached
and notes generated from the commits.

## How the updater finds it

Settings -> Updates -> **Update repository** is an `owner/repo` string; it defaults to `bendikme/DynatecRDM`.
The app asks that repository's GitHub releases API for the newest release and downloads the asset
ending in `.msi`, so every release must attach exactly one. The release tag is the version it
compares against its own `InformationalVersion`, which `build\publish.ps1` stamps from `-Version`.
A private repository also needs a token in the update settings.

Installing an update closes the app first, runs `msiexec /i <msi>` visibly, and the package's
`LaunchApplication` action starts the new build when the install finishes.

## Notes

- The `UpgradeCode` GUID in `DynatecRDM.wxs` must never change: it is what makes a new build
  replace the old one instead of installing beside it.
- Silent install: `msiexec /i DynatecRDM-1.2.3-x64.msi /qn /norestart`. Exit the running app
  first; a silent install deliberately does not relaunch it.
- `LAUNCHATLOGON=1` starts the app with Windows (off by default; the app's own setting can turn it
  on or off later, and an upgrade keeps whatever is set). `LAUNCHAPP=0` stops a visible install
  from starting the app.
