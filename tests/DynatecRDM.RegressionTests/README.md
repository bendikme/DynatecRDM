# Windows regression checks

Run from the repository root:

```powershell
dotnet run --project tests/DynatecRDM.RegressionTests -c Release
```

The executable exits nonzero on a failed assertion. The default suite uses WPF and the installed
Windows RDP ActiveX control, creates off-screen windows without activating them, and uses only
synthetic credentials. Its transport test calls the real `Connect()` method and verifies an RDP
negotiation packet reaches a temporary loopback listener. It never authenticates to an RDP server
or opens the user's application database.
The token-storage checks create and remove a uniquely named database beside the test executable.

Coverage includes rendered tab buttons, accumulated mouse-wheel input, disconnect confirmation,
stale tabs, native mouse activation, interrupted hide animations, session/window identity,
hidden and ended sessions, reveal-strip boundaries at 100/125/150/200% scale and negative monitor
coordinates, full-screen minimize/restore, native-bar fallback, reused RDP
credentials/settings, UI launch error propagation, DPAPI token storage and migration, and actual
RDP transport startup. Reading settings back from a disconnected control is insufficient to
establish that it can connect: enabling `ShowRedirectionWarningDialog` passed the property checks
but made `Connect()` abort locally with reason 1.

An explicit live test is also available:

```powershell
dotnet run --project tests/DynatecRDM.RegressionTests -c Release -- --live "Connection name"
```

This authenticates to the named saved connection, using its existing credential and authentication
settings, and opens a real window. It tests login, bar reveal, minimize/restore, leaving full screen,
and reconnecting. It disconnects its test session afterwards. The database is read-only, no launch
history is written, and no snapshots are taken. Run it only against a connection authorized for
testing; logging in can displace an existing session for the same account.

For a saved **Windows 10 desktop with its Start button at the bottom left**, the pointer test is:

```powershell
dotnet run --project tests/DynatecRDM.RegressionTests -c Release -- --live-hover "Connection name"
```

This briefly controls the local pointer; leave the mouse and keyboard idle while it runs. It clicks
the remote Start button open and closed three times (checking that the receiving window belongs to
the test RDP session), then reveals the bar solely by hovering at 0, 3, 8, and 11 pixels below the top.
It checks actual native hit testing and unchanged keyboard focus, not just the bar's visible flag.
It also raises the RDP window above the revealed bar and requires hover to recover it, and holds
the pointer on the reveal strip beside the bar to check that it stays reachable. The original pointer
position and foreground window are restored afterwards. No screenshots are saved.

To check a fast movement all the way to the screen boundary:

```powershell
dotnet run --project tests/DynatecRDM.RegressionTests -c Release -- --live-edge "Connection name"
```

This first places a three-pixel test window over the top edge of the connected session. Both the
exact top row and a point eight pixels below it must still select RDP; a larger overlay or another
foreground window must not. It then makes six single upward moves beyond the screen boundary,
including after opening and closing remote Start. Unlike `--live-hover`, it does not repeatedly
park the pointer: the bar must receive native hit testing while the pointer is still on the exact
top row. Pointer movement before the reveal fails the check as interrupted.

Cross-monitor foreground switching, mixed physical monitor DPI, remote resolution negotiation,
and external mstsc/msrdc toolbar behavior still need interactive checks.
