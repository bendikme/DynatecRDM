# Debug and safety review — 2026-09-21

## Fixed

- The WPF message loop now includes the WinForms input preprocessor required by modeless RDP
  windows. The session bar explicitly declines mouse activation while accepting clicks.
- Tab switching verifies the actual foreground session. A failed focus or switch to another
  monitor/windowed session hides the bar and clears its action target. Hidden, minimized,
  disconnected, and ended sessions cannot leave a usable stale bar behind.
- Session identity checks include the owning process and current top-level handle. Embedded
  windows update their published handle when WinForms recreates it. Native bar restoration
  checks the owning process and class before changing a saved handle.
- Top-edge dwell works with vertically stacked monitors and restarts when its target changes.
  A failed reveal clears its visible state and restores native controls. Disabling the custom
  bar enables the embedded client's native controls.
- The reveal strip is twelve DIPs tall, scaled per monitor. It keeps the bar reachable while the
  pointer moves sideways along the strip. If a full-screen RDP window rises above an already
  revealed bar, hovering raises the bar again without activating it or waiting for a click.
  Recovery only raises over a recognized full-screen session.
- Exact-top detection also checks immediately below the reveal strip when a thin edge window
  intercepts the pointer. It accepts only the foreground session, covering that monitor and
  visible at the second point. The real pointer stays at the screen boundary; another foreground
  app or a larger overlay prevents this fallback.
- Minimizing preserves full-screen layout, restores topmost state when restored, and avoids
  resizing the remote desktop to a minimized window. Exiting full screen restores a previously
  maximized window correctly.
- Embedded launch failures and cancellation now propagate to cleanup. Sessions are registered
  before connecting, concurrent launches are serialized to prevent duplicates, and shutdown
  closes runtimes before delayed reconnects can revive them. Reconnect suppression applies only
  to deliberate teardown, so a failure during the new connection is still handled.
- Reused RDP controls clear passwords, usernames, domains, alternate shells, gateway hostnames,
  and routing tokens before applying current settings. Gateway configuration and clearing drive
  redirection must succeed before connecting. Server-authentication policy and NLA follow the
  saved connection's settings.
- Private-repository update tokens are DPAPI-protected in settings. Older plaintext values are
  migrated when loaded. The in-memory UI value remains usable; damaged ciphertext is never sent
  as a bearer token. Existing backups or old SQLite pages may retain previously stored plaintext.

## Verification

`dotnet run --project tests/DynatecRDM.RegressionTests -c Release` exercises the real WPF bar and
disconnected RDP control, plus a temporary SQLite database. See the test README for exact scope.
The NuGet vulnerable-package scan (including transitive dependencies) reported no known vulnerable
packages from the configured sources. This does not audit the pre-generated COM wrapper binaries.

The original nine checks missed a connection-breaking regression: setting
`ShowRedirectionWarningDialog=true` made the embedded client abort before opening a socket, with
disconnect reason 1 and an internal-error description. The property read-back check incorrectly
treated that setting as safe. The setting has been corrected, and the default suite now has a tenth
check that calls `Connect()` and requires a real RDP negotiation packet at a loopback listener.

Following the reported failure, the saved Win10 connection was tested live through
`EmbeddedSessionManager`: login completed, the bar's minimize/restore/exit-full-screen actions
preserved the connected session, and reconnect completed a second login. Only the test session was
closed afterwards. The live harness reads settings/credentials without changing the database or
writing snapshots. Physical mixed-DPI layouts and external client toolbar behavior remain manual
checks. Early reason-1 disconnects are now recorded as failures, with their reason in the log,
instead of being reported as successful user disconnects.

The hover failure was reproduced with a stationary pointer: after raising the RDP window, the bar
reported itself revealed but native hit testing reached RDP instead. The old implementation did
not recover; the fix does. Live hover checks on Win10 now open and close Start **inside the remote
desktop**, then reveal without clicking at several offsets from the edge, retaining RDP keyboard
focus. The default suite additionally checks scaled reveal geometry and now has eleven checks.

A further controlled live test reproduced a top-row dead zone using a three-pixel window over
RDP: the old code rejected the exact top but accepted a point eight pixels below it. Both points
now pass, while larger overlays and other foreground windows remain excluded. Six rapid upward
movements to the actual screen boundary passed at 96 DPI, including after remote Start was opened
and closed. Native hit testing reached the bar in 218–281 ms with the pointer still at the top,
without downward nudges or repeated position resets during the reveal.

## Remaining findings

- The updater restricts download origins to HTTPS GitHub hosts and checks file size, but does not
  verify the release's SHA256SUMS or an Authenticode publisher. Its trust boundary is the configured
  GitHub repository and TLS. A compromised release account can supply executable code.
- Selecting the optional `cmdkey` credential-writing mode still puts the password in a process
  command line. The default native credential API avoids this; removing the legacy mode would be
  a separate compatibility change.
- Session thumbnails are ordinary image files and can contain sensitive remote-screen content.
  Snapshot storage is governed by the existing snapshot settings, not DPAPI.
- DPAPI protects secrets at rest for the Windows user. It does not protect against another
  process already running as that user. The fixed application entropy is not a secret.

## API references

- [WPF modeless WinForms input integration](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.integration.windowsformshost.enablewindowsformsinterop)
- [ResetPassword clears password state before reusing an RDP control](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscnonscriptable-resetpassword)
- [RDP credential warning setting](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable3-warnaboutsendingcredentials)
- [Native connection-bar visibility](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable5-disableconnectionbar)
