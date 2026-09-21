using System.Net;
using System.Net.Sockets;

namespace DynatecRDM.Services;

/// <summary>What the probe found out about one step, as it happens.</summary>
public enum ProbeStep
{
    AddressFound,
    AddressFailed,
    PortOpen,
    PortFailed,
}

/// <summary>
/// The first two steps of a connection, checked by the app itself alongside the Remote Desktop
/// control: finding the address, and whether the port answers. The control does both too but says
/// nothing until it has finished or failed, so this is what lets the connecting screen show where
/// a slow connection is stuck. It only reports; the control's own result decides the connection.
/// </summary>
public static class ConnectionProbe
{
    /// <summary>
    /// Resolves <paramref name="host"/> and opens (and at once closes) a TCP connection to its port,
    /// calling <paramref name="report"/> with each result and a short detail - the address found, or
    /// why a step failed. Never throws; cancellation just stops it.
    /// </summary>
    public static async Task RunAsync(
        string host, int port, TimeSpan portTimeout, Action<ProbeStep, string?> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        var (target, resolvedPort) = RdpConnectivity.Normalize(host ?? string.Empty, port);
        if (target.Length == 0)
        {
            report(ProbeStep.AddressFailed, null);
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(target, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            report(ProbeStep.AddressFailed, ex is SocketException se ? se.SocketErrorCode.ToString() : ex.Message);
            return;
        }

        if (ct.IsCancellationRequested) return;
        if (addresses.Length == 0)
        {
            report(ProbeStep.AddressFailed, null);
            return;
        }

        // IPv4 first when there is one: it is what an RDP server is by far most often listening on.
        var ordered = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        report(ProbeStep.AddressFound, ordered[0].ToString());

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(portTimeout);
            using var client = new TcpClient(ordered[0].AddressFamily);
            await client.ConnectAsync(ordered[0], resolvedPort, timeout.Token).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            report(ProbeStep.PortOpen, resolvedPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) return;
            report(ProbeStep.PortFailed, null);   // no answer within the time allowed
        }
        catch (SocketException ex)
        {
            if (ct.IsCancellationRequested) return;
            report(ProbeStep.PortFailed, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            AppLog.Debug_($"Probing {target}:{resolvedPort} failed: {ex.Message}");
            report(ProbeStep.PortFailed, null);
        }
    }
}
