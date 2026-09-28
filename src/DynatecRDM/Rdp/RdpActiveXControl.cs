using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AxMSTSCLib;
using MSTSCLib;

namespace DynatecRDM.Rdp;

/// <summary>
/// Owns the RDP event connection explicitly. The .NET 10.0.12 AxHost.ConnectionPointCookie
/// leaves an extra COM reference to the generated event sink, rooting the control and its
/// window even after Dispose. Use the COM interop marshaler to balance the sink references.
/// </summary>
internal sealed class RdpActiveXControl : AxMsRdpClient11NotSafeForScripting
{
    private IConnectionPoint? _events;
    private int _cookie;

    protected override void CreateSink()
    {
        DetachSink();
        var eventId = typeof(IMsTscAxEvents).GUID;
        var source = GetOcx() as IConnectionPointContainer
            ?? throw new InvalidOperationException("The RDP control has no event connection points.");
        source.FindConnectionPoint(ref eventId, out var events);
        if (events is null) throw new InvalidOperationException("The RDP event connection point is unavailable.");
        try
        {
            events.Advise(new AxMsRdpClient11NotSafeForScriptingEventMulticaster(this), out _cookie);
            _events = events;
        }
        catch
        {
            Marshal.ReleaseComObject(events);
            throw;
        }
    }

    protected override void DetachSink()
    {
        var events = _events;
        if (events is null) return;
        _events = null;
        try { events.Unadvise(_cookie); }
        finally
        {
            _cookie = 0;
            Marshal.ReleaseComObject(events);
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing) DetachSink();
        }
        finally { base.Dispose(disposing); }
    }
}
