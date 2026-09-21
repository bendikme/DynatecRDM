# Remote Desktop Manager

A fast Windows manager for Remote Desktop sessions. It stores connections in a local database,
launches them through Windows' own Remote Desktop Connection (`mstsc.exe`), and keeps them alive
with a reconnect watchdog. It lives in the notification area with a quick-launch menu that shows a
live thumbnail of every running session.

## What it does

- **Launches real RDP sessions.** Every connection is written to a complete `.rdp` file and opened
  with `mstsc.exe`, so there is no third-party RDP client to trust or maintain.
- **Exposes the settings Remote Desktop Connection hides.** Multi-monitor selection
  (`use multimon`, `selectedmonitors`), per-monitor full screen, window placement, scaling factors,
  RemoteApp, RD Gateway, drive and camera redirection, and a raw key/value editor with a live
  preview of the exact `.rdp` file that will be generated.
- **Full screen and window, done right.** A session on one monitor starts full screen at that
  monitor's own resolution and knows the window to drop back to, so Ctrl+Alt+Break (or the session
  bar) switches cleanly between the two - no more sessions stuck at the wrong size. A window can
  also be placed exactly by dragging and resizing it on a map of your monitors.
- **True dynamic resolution, optionally.** The built-in Remote Desktop client (`mstsc`) only scales
  the remote desktop to the window. When the modern **Remote Desktop client** (`msrdc`, from
  `winget install Microsoft.RemoteDesktopClient`) is installed, Settings > Remote Desktop client can
  launch through it instead, and the remote resolution then changes to match as you resize the
  window or leave full screen - the same behaviour as the macOS Windows App. Sessions keep their
  live thumbnails, placement and session bar either way, and fall back to `mstsc` when the modern
  client is not present.
- **Groups connections** into nested folders, with search, favourites and colour coding.
- **Reuses credentials.** A credential set can be shared by many connections. Passwords are stored
  DPAPI-protected and are never written to disk in clear.
- **Remembers logins the way Windows does.** It writes the `TERMSRV/<host>` entry in the Windows
  Credential Vault - the same store `cmdkey` uses - and can also embed an encrypted password in the
  `.rdp` file.
- **Multi-configs.** Launch several connections together, each with its own screen placement that
  overrides the connection's own settings. A layout map shows which session lands on which monitor.
- **Tray quick launch.** A global shortcut (Ctrl+Alt+R by default) opens a searchable menu of every
  connection and multi-config, marking the ones already connected and showing a thumbnail of each
  live session. Anything running has a **Disconnect** button, and Delete ends the highlighted one.
  The main window's live-session cards show the same thumbnails.
- **Session bar.** Rest the pointer on the top edge of a full-screen session and a slim bar slides
  down with a tab for every running connection: one click (or a scroll) swaps which one fills the
  screen, so several full-screen sessions on one monitor behave like tabs of a single window. It
  also minimises, leaves full screen and disconnects, and replaces Remote Desktop's own connection
  bar. On by default; Settings > Full screen turns it off.
- **Reconnect watchdog.** Sessions that drop - or that never came up because the far end was still
  booting - are relaunched, up to a per-connection attempt limit.
- **Export and import.** Move single connections, a selection, or the whole library between machines
  as a `.drdm` file, and back up the database.
- **Updates itself** from GitHub releases, if you point it at a repository.
- **Light and dark.** Follows the Windows app mode (Settings > Personalisation > Colours) and
  switches live when it changes, title bars and tray menu included. Both palettes meet WCAG AA
  contrast, and the accent chosen in Settings is adjusted per theme until it does too.
- **English and Norwegian.** Speaks Norwegian (Bokmål) when Windows does and English otherwise,
  or whichever you pick under **Settings > Language**. A change applies as soon as you save.

## Requirements

- Windows 10 1809 or newer, x64
- Nothing else. The installer carries its own .NET runtime.

## Installing

Run `DynatecRDM-<version>-x64.msi`. It installs per-user into
`%LOCALAPPDATA%\Programs\DynatecRDM` and needs no administrator rights, which is also what lets the
app update itself silently. There is a portable `.zip` if you would rather not install.

Your data lives in `%LOCALAPPDATA%\DynatecRDM` - database, session thumbnails and log. That folder
is deliberately outside the install folder, so installing, updating, repairing and uninstalling
never touch it.

## Building

```powershell
dotnet build src\DynatecRDM\DynatecRDM.csproj      # development build
dotnet run   --project src\DynatecRDM\DynatecRDM.csproj
```

To produce the installer and the portable zip:

```powershell
dotnet tool install --global wix --version 5.0.2   # once; v6+ requires accepting a paid EULA
.\build\release.ps1 -Version 1.2.3
```

Artifacts land in `artifacts\`. See `installer\README.md` for how releases are cut.

## Turning on updates

Update checking ships switched off because it needs to know where your releases live. In
**Settings > Updates**, set **Release repository** to `owner/repo`. An access token is only needed
for a private repository. Releases are published by tagging `v<version>`, which runs
`.github\workflows\release.yml` and attaches the `.msi`, the `.zip` and `SHA256SUMS.txt`.

## How it is put together

| Folder | What is in it |
| --- | --- |
| `src\DynatecRDM\Models` | The stored entities and their settings objects |
| `src\DynatecRDM\Data` | SQLite store, schema and versioned migrations |
| `src\DynatecRDM\Services` | `.rdp` generation, credentials, monitors, snapshots, sessions, updates, transfer |
| `src\DynatecRDM\Interop` | The Win32, credential, DPAPI and monitor P/Invokes |
| `src\DynatecRDM\ViewModels` / `Views` / `Themes` | WPF UI and the light/dark design system (`Themes\Palette.*.xaml` hold the colours) |
| `src\DynatecRDM\Resources` | Every piece of UI text: `Strings.resx` (English) and `Strings.nb.resx` (Norwegian). A new string goes into both |
| `installer`, `build`, `.github\workflows` | Packaging and release |

## A note on `cmdkey`

The documented way to cache an RDP login is:

```
cmdkey /list:TERMSRV/*
cmdkey /delete:TERMSRV/<host>
cmdkey /generic:TERMSRV/<host> /user:<user> /pass:<password>
```

This app does the same thing through the Windows credential API instead, for two reasons. The
password never appears on a command line, where any process that can read another process's command
line could see it. And `cmdkey` cannot carry a password containing a space or a quote: it silently
falls back to prompting, stores an **empty** password, and still prints "Credential added
successfully" and exits 0. The `cmdkey` route is still selectable in Settings, and falls back to the
API for passwords it cannot carry.
