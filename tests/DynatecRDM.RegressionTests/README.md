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

## Memory regression checks

The default suite also repeatedly creates and closes real RDP ActiveX windows with WPF overlays,
then checks weak references after dispatcher teardown and garbage collection. Closed windows,
controls, and overlay content must all become collectible. It also measures allocations while a
solid overlay is resized between portrait and landscape and hidden/shown six times. Run these checks with:

```powershell
dotnet run --project tests/DynatecRDM.RegressionTests -c Release -- --memory
```

This caught a COM event sink retaining every disposed RDP window on .NET 10.0.12. Before the fix,
16 of 24 tracked objects survived eight open/close cycles; afterwards none survived. The transport
check additionally verifies that the replacement subscription still delivers the connecting event.
Forced GC is used only to make the test deterministic, not as an application memory workaround.

The follow-up allocation check caught a separate issue: ElementHost's default background mapping
creates full-window bitmaps even for a solid colour, including on size and visibility changes.
Six resize/show cycles allocated 294.7 MiB before removing those mappings and approximately 0.1 MiB
afterwards. The WPF progress view supplies its own opaque background. The check allows 16 MiB for
framework/layout variation and verifies that the overlay still lays out and becomes visible.
See the [framework background mapping](https://source.dot.net/WindowsFormsIntegration/System/Windows/Integration/ElementHostPropertyMap.cs.html).

In the running 1.0.10 process's follow-up dump, the closed session window had no GC roots, but two
14 MiB bitmap buffers were still rooted through WPF's StreamAsIStream handles. This supports
eliminating the unnecessary image allocations; a single dump does not establish an unbounded leak.
The allocation test does not authenticate or measure a connected RDP client's native memory.

For release validation, record a fresh process's working set, private bytes, managed heap, and
handle count after warm-up. Repeat opening/closing the manager, quick launch, editors, and approved
RDP sessions, then return to the same idle state between batches. Include failed connections and
reconnects. Compare several batches and an overnight idle run: retained objects and resources
should plateau, rather than grow with each cycle. Active RDP sessions are a separate baseline.

Use `dotnet-counters collect --process-id <PID> --counters System.Runtime --format csv
--duration 00:10:00 --output idle.csv` for runtime metrics. If the managed heap grows, compare local
heap dumps using `dotnet-dump` (`dumpheap -stat` and `gcroot`). If private bytes or handles grow
without managed-heap growth, investigate native RDP, GDI, and WPF resources too. Heap dumps can
contain credentials and desktop content; keep them local and outside source control.

The September 2026 investigation of the six-day-old 1.0.9 process found approximately 1.46 GiB
working set, 753 MiB GC committed memory, and 581 MiB in large byte arrays. A disposed RDP window
was rooted by its generated COM event multicaster; large image buffers were also rooted through
ElementHost background brushes. The checked-in fix owns the RDP COM event connection explicitly,
balancing Advise/Unadvise instead of using the leaking AxHost connection cookie in that runtime.
The regression establishes collectibility for this path; it does not establish that every app
workflow is leak-free or predict a fresh process's exact memory footprint.
