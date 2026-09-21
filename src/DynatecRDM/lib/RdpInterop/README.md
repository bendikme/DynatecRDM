# RDP ActiveX interop assemblies

`MSTSCLib.dll` and `AxMSTSCLib.dll` are interop wrappers for the Remote Desktop ActiveX
control (`mstscax.dll`). The app **hosts this control in-process** and connects
programmatically, rather than launching `mstsc.exe`/`msrdc.exe` as external processes.

## Why they are checked in

The .NET SDK build (`dotnet build`) cannot generate COM interop itself — the
`ResolveComReference` MSBuild task is not implemented on the .NET Core toolchain
(error `MSB4803`), so a `<COMReference>` fails outside the legacy .NET Framework MSBuild.
The two assemblies are therefore generated once with the Windows SDK tools and referenced
as ordinary assemblies (see `DynatecRDM.csproj`).

They wrap the stable, long-lived RDP ActiveX interfaces (`IMsRdpClient` … `IMsRdpClient10`),
so they do not need regenerating for routine `mstscax.dll` servicing updates.

## How to regenerate

From a machine with the Windows SDK (`v10.0A` NETFX tools), run:

```
set TOOLS=C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64
"%TOOLS%\TlbImp.exe" C:\Windows\System32\mstscax.dll /out:MSTSCLib.dll /namespace:MSTSCLib /silent
"%TOOLS%\AxImp.exe"  C:\Windows\System32\mstscax.dll
```

`AxImp` emits both `AxMSTSCLib.dll` (the `System.Windows.Forms.AxHost` wrappers, e.g.
`AxMsRdpClient11NotSafeForScripting`) and its `MSTSCLib.dll` dependency; copy both here.

## Control/CLSID note

Use the **`AxMsRdpClient11NotSafeForScripting`** wrapper. Its coclass CLSID
`{1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8}` is the newest control that actually instantiates
on current Windows — the even-newer registered `{3F859AA3-…}` (v13) returns
`CLASS_E_CLASSNOTAVAILABLE`. The wrapper's OCX casts to `IMsRdpClient9`/`IMsRdpClient10`,
which expose `UpdateSessionDisplaySettings` (dynamic resolution). The *NotSafeForScripting*
variant is required so credentials (`ClearTextPassword`) can be set programmatically.
