using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DynatecRDM.Services;

/// <summary>
/// Cheap host probes. Used for the live reachability dot in the UI and by the watchdog, which
/// refuses to relaunch mstsc against a host that is not answering yet.
/// </summary>
public static class RdpConnectivity
{
    public const int DefaultRdpPort = 3389;

    /// <summary>
    /// True when the host accepts a TCP connection on the port within the timeout. Never throws:
    /// DNS failures, refusals, cancellation and timeouts all come back as false.
    /// </summary>
    public static async Task<bool> IsReachableAsync(
        string host,
        int port,
        int timeoutMs = 1200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var (target, resolvedPort) = Normalize(host, port);
        if (target.Length == 0) return false;
        if (timeoutMs < 50) timeoutMs = 50;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            using var client = new TcpClient();
            await client.ConnectAsync(target, resolvedPort, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reachability probe for {target}:{resolvedPort} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Probes the port embedded in the address, falling back to the standard RDP port.</summary>
    public static Task<bool> IsReachableAsync(string host, CancellationToken ct = default) =>
        IsReachableAsync(host, 0, 1200, ct);

    /// <summary>ICMP round trip in milliseconds. Never throws; ok is false when the host stays silent.</summary>
    public static async Task<(bool ok, long ms)> PingAsync(string host, int timeoutMs = 1200)
    {
        if (string.IsNullOrWhiteSpace(host)) return (false, 0L);
        if (timeoutMs < 50) timeoutMs = 50;

        var (target, _) = Normalize(host, DefaultRdpPort);
        if (target.Length == 0) return (false, 0L);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? (true, reply.RoundtripTime)
                : (false, 0L);
        }
        catch (PingException)
        {
            return (false, 0L);
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Ping to {target} failed: {ex.Message}");
            return (false, 0L);
        }
    }

    /// <summary>
    /// Splits "host:port" and "[v6]:port" addresses. An explicit, valid <paramref name="port"/>
    /// wins over an embedded one; a bare IPv6 literal (more than one colon) is left untouched.
    /// </summary>
    private static (string Host, int Port) Normalize(string host, int port)
    {
        var target = host.Trim();
        var embedded = 0;

        if (target.Length > 1 && target[0] == '[')
        {
            var close = target.IndexOf(']');
            if (close > 0)
            {
                var rest = target.AsSpan(close + 1);
                if (rest.Length > 1 && rest[0] == ':') int.TryParse(rest[1..], out embedded);
                target = target[1..close];
            }
        }
        else
        {
            var colon = target.LastIndexOf(':');
            if (colon > 0 && colon == target.IndexOf(':')
                && int.TryParse(target.AsSpan(colon + 1), out embedded))
            {
                target = target[..colon];
            }
        }

        if (port is <= 0 or > 65535)
            port = embedded is > 0 and <= 65535 ? embedded : DefaultRdpPort;

        return (target, port);
    }
}
