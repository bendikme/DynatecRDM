using Microsoft.Win32;

namespace DynatecRDM.Services;

/// <summary>Whether Windows will pass the signed-in user's own credentials to a host.</summary>
public enum WindowsSignInStatus
{
    /// <summary>The policy names the host: Remote Desktop signs in without asking.</summary>
    Allowed,
    /// <summary>The policy does not name the host: Remote Desktop asks for the password.</summary>
    NotAllowed,
    /// <summary>
    /// An IP address. The policy is about computers Windows can verify through the domain, which
    /// takes a name; an address always gets the password prompt.
    /// </summary>
    NeedsComputerName,
}

/// <summary>
/// The CredSSP policy "Allow delegating default credentials" decides whether Remote Desktop may
/// hand the Windows sign-in to a computer. It is off unless an administrator sets it, and without
/// it signing in as the Windows user ends at the password prompt, for mstsc and the in-process
/// control alike. This only reads the policy, so the editor can say what will happen; changing
/// it is left to the administrator and Group Policy.
/// </summary>
public static class CredentialDelegationPolicy
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";
    private const string ListName = "AllowDefaultCredentials";

    public static WindowsSignInStatus StatusFor(string? host)
    {
        var name = HostOnly(host);
        if (name.Length == 0 || IsAddress(name)) return WindowsSignInStatus.NeedsComputerName;
        return Allows(name) ? WindowsSignInStatus.Allowed : WindowsSignInStatus.NotAllowed;
    }

    /// <summary>True when the enabled policy lists the host, directly or through a wildcard.</summary>
    private static bool Allows(string host)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (key?.GetValue(ListName) is not int enabled || enabled != 1) return false;

            using var list = key.OpenSubKey(ListName);
            if (list is null) return false;
            foreach (var valueName in list.GetValueNames())
            {
                if (list.GetValue(valueName) is string pattern && Matches(pattern, host)) return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Could not read the default-credentials policy: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// One policy entry against a host. Entries read "service/host", where either part may be "*"
    /// and the host may start with "*." to cover a whole domain - the forms the policy documents.
    /// </summary>
    internal static bool Matches(string pattern, string host)
    {
        var text = pattern.Trim();
        var slash = text.IndexOf('/');
        if (slash <= 0) return false;

        var service = text[..slash];
        if (service != "*" && !service.Equals("TERMSRV", StringComparison.OrdinalIgnoreCase)) return false;

        var wanted = text[(slash + 1)..];
        if (wanted == "*") return true;
        if (wanted.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(wanted[1..], StringComparison.OrdinalIgnoreCase);
        return host.Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The host without a port, as Kerberos names the computer.</summary>
    private static string HostOnly(string? host)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.StartsWith('[')) return text;               // bracketed IPv6; IsAddress says so
        var colon = text.IndexOf(':');
        if (colon >= 0 && text.IndexOf(':', colon + 1) < 0) text = text[..colon];
        return text;
    }

    private static bool IsAddress(string host) =>
        host.StartsWith('[') || System.Net.IPAddress.TryParse(host, out _);
}
