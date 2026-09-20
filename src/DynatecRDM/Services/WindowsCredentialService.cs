using System.Diagnostics;
using System.IO;
using DynatecRDM.Interop;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Reads and writes the TERMSRV/&lt;host&gt; entries mstsc looks for in the Windows Credential Vault.
/// The native path is the default because the password never reaches a command line, where any
/// process able to read the command line of a running process could pick it up, and because
/// nothing has to be spawned - a vault write costs microseconds instead of a process launch.
/// </summary>
public sealed class WindowsCredentialService : IWindowsCredentialService
{
    private const string TermsrvPrefix = "TERMSRV/";
    private const string TermsrvFilter = "TERMSRV/*";
    private const int CmdKeyTimeoutMs = 10_000;

    private static readonly string CmdKeyPath = ResolveCmdKeyPath();

    public IReadOnlyList<StoredCredentialInfo> ListTermsrvCredentials()
    {
        try
        {
            var raw = CredentialApi.Enumerate(TermsrvFilter);
            if (raw.Count == 0) return [];

            var seen = new HashSet<(string Host, uint Type)>(raw.Count);
            var list = new List<StoredCredentialInfo>(raw.Count);

            for (var i = 0; i < raw.Count; i++)
            {
                var entry = raw[i];
                var target = entry.TargetName;
                if (string.IsNullOrEmpty(target) ||
                    !target.StartsWith(TermsrvPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var host = target[TermsrvPrefix.Length..].Trim();
                if (host.Length == 0) continue;

                if (!seen.Add((host.ToLowerInvariant(), entry.Type))) continue;

                list.Add(new StoredCredentialInfo(
                    target,
                    host,
                    string.IsNullOrWhiteSpace(entry.UserName) ? null : entry.UserName,
                    TypeName(entry.Type),
                    PersistName(entry.Persist),
                    entry.LastWrittenUtc));
            }

            list.Sort(static (a, b) =>
            {
                var c = string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.TypeName, b.TypeName);
            });

            return list;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Enumerating TERMSRV credentials failed.", ex);
            return [];
        }
    }

    public bool Exists(string host)
    {
        var target = BuildTarget(host);
        if (target is null) return false;

        try
        {
            return CredentialApi.Read(target, CredentialApi.CRED_TYPE_DOMAIN_PASSWORD) is not null
                || CredentialApi.Read(target, CredentialApi.CRED_TYPE_GENERIC) is not null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Credential lookup failed for {target}.", ex);
            return false;
        }
    }

    public string? GetStoredUsername(string host)
    {
        var target = BuildTarget(host);
        if (target is null) return null;

        try
        {
            var domain = CredentialApi.Read(target, CredentialApi.CRED_TYPE_DOMAIN_PASSWORD);
            if (domain is not null && !string.IsNullOrWhiteSpace(domain.UserName)) return domain.UserName;

            var generic = CredentialApi.Read(target, CredentialApi.CRED_TYPE_GENERIC);
            if (generic is not null && !string.IsNullOrWhiteSpace(generic.UserName)) return generic.UserName;

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the stored user name failed for {target}.", ex);
            return null;
        }
    }

    public bool SaveCredential(string host, string username, string password, VaultWriteMethod method)
    {
        var target = BuildTarget(host);
        if (target is null)
        {
            AppLog.Error("SaveCredential was called without a host name.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            AppLog.Error($"SaveCredential for {target} was called without a user name.");
            return false;
        }

        password ??= string.Empty;

        return method == VaultWriteMethod.CmdKey
            ? SaveViaCmdKey(target, username, password)
            : SaveViaNativeApi(target, username, password);
    }

    public bool DeleteCredential(string host, VaultWriteMethod method = VaultWriteMethod.NativeCredentialApi)
    {
        var target = BuildTarget(host);
        if (target is null) return false;

        if (method == VaultWriteMethod.CmdKey)
        {
            var removed = CmdKeyDelete(target, 2);
            if (removed > 0) AppLog.Info($"cmdkey removed {removed} vault entry/entries for {target}.");
            return removed > 0;
        }

        var count = DeleteBothTypes(target);
        if (count > 0) AppLog.Info($"Removed {count} vault entry/entries for {target}.");
        return count > 0;
    }

    private static bool SaveViaNativeApi(string target, string username, string password)
    {
        try
        {
            DeleteBothTypes(target);

            // mstsc looks for the domain-password entry first; the generic one is the exact
            // equivalent of "cmdkey /generic" and keeps older stacks and RD Gateway happy.
            var domain = WriteWithPersistFallback(target, username, password, CredentialApi.CRED_TYPE_DOMAIN_PASSWORD);
            var generic = WriteWithPersistFallback(target, username, password, CredentialApi.CRED_TYPE_GENERIC);

            if (!domain && !generic)
            {
                AppLog.Error($"Writing the vault entry for {target} failed for both credential types.");
                return false;
            }

            AppLog.Info($"Vault entry written for {target} (domain={domain}, generic={generic}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Writing the vault entry for {target} threw.", ex);
            return false;
        }
    }

    private static bool WriteWithPersistFallback(string target, string username, string password, uint type)
    {
        if (CredentialApi.Write(target, username, password, type, CredentialApi.CRED_PERSIST_ENTERPRISE))
            return true;

        if (CredentialApi.Write(target, username, password, type, CredentialApi.CRED_PERSIST_LOCAL_MACHINE))
        {
            AppLog.Warn($"Enterprise persistence was refused for {target} ({TypeName(type)}); stored locally instead.");
            return true;
        }

        return false;
    }

    private static int DeleteBothTypes(string target)
    {
        var count = 0;
        try
        {
            if (CredentialApi.Delete(target, CredentialApi.CRED_TYPE_DOMAIN_PASSWORD)) count++;
            if (CredentialApi.Delete(target, CredentialApi.CRED_TYPE_GENERIC)) count++;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Deleting the vault entries for {target} failed.", ex);
        }
        return count;
    }

    /// <summary>
    /// cmdkey.exe cannot carry these through /pass: on a command line. A quoted value such as
    /// /pass:"a b c" is not understood by its parser, and quoting the whole argument instead
    /// (which is the only thing ProcessStartInfo.ArgumentList can produce) is rejected outright.
    /// In both cases cmdkey falls back to prompting on a console that does not exist here, stores
    /// an EMPTY password, and still prints "Credential added successfully" and exits 0.
    /// </summary>
    private static bool CmdKeyCanCarry(string password)
    {
        for (var i = 0; i < password.Length; i++)
        {
            var c = password[i];
            if (char.IsWhiteSpace(c) || c == '"') return false;
        }
        return true;
    }

    private static bool SaveViaCmdKey(string target, string username, string password)
    {
        // "cmdkey /pass:" with an empty value makes cmdkey prompt for the password on its console.
        // There is no console here, so it would stall until the timeout and store nothing.
        if (password.Length == 0)
        {
            AppLog.Warn($"An empty password cannot be stored through cmdkey; {target} was written with the credential API instead.");
            return SaveViaNativeApi(target, username, password);
        }

        if (!CmdKeyCanCarry(password))
        {
            AppLog.Warn(
                $"This password contains a space or a quote, which cmdkey cannot accept on a command " +
                $"line - it would store an empty password and still report success. {target} was " +
                $"written with the credential API instead.");
            return SaveViaNativeApi(target, username, password);
        }

        CmdKeyDelete(target, 2);

        var exitCode = RunCmdKey(
            $"cmdkey /generic:{target}",
            $"/generic:{target}",
            $"/user:{username}",
            $"/pass:{password}");

        // cmdkey exits 0 even when it rejects its parameters, so the exit code proves nothing on
        // its own. The entry has to be read back before this can be called a success.
        if (exitCode == 0 && CredentialApi.Read(target, CredentialApi.CRED_TYPE_GENERIC) is not null)
        {
            AppLog.Info($"cmdkey stored the credential for {target}.");
            return true;
        }

        AppLog.Warn(
            $"cmdkey did not store a usable entry for {target} (exit code {exitCode}); " +
            $"falling back to the credential API.");
        return SaveViaNativeApi(target, username, password);
    }

    private static int CmdKeyDelete(string target, int maxAttempts)
    {
        var removed = 0;
        for (var i = 0; i < maxAttempts; i++)
        {
            var exitCode = RunCmdKey($"cmdkey /delete:{target}", $"/delete:{target}");
            if (exitCode != 0) break;
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Runs cmdkey.exe with every argument passed separately, so a password containing spaces or
    /// quotes cannot change the shape of the command. <paramref name="label"/> is the only thing
    /// ever logged - the argument list may hold a password and must never reach the log.
    /// </summary>
    private static int RunCmdKey(string label, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CmdKeyPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        for (var i = 0; i < arguments.Length; i++)
            psi.ArgumentList.Add(arguments[i]);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                AppLog.Error($"{label} could not be started.");
                return -1;
            }

            // Both pipes are redirected, so they have to be drained or a chatty child could block.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Closing stdin turns any unexpected interactive prompt into an immediate EOF instead
            // of a stall that only the timeout would end.
            try { process.StandardInput.Close(); } catch { }

            if (!process.WaitForExit(CmdKeyTimeoutMs))
            {
                AppLog.Error($"{label} did not finish within {CmdKeyTimeoutMs} ms; the process was killed.");
                try { process.Kill(entireProcessTree: true); } catch { }
                return -2;
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Error($"{label} failed to run.", ex);
            return -1;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Normalises whatever the caller passed into "TERMSRV/&lt;bare host&gt;", or null.</summary>
    private static string? BuildTarget(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var value = host.Trim();

        if (value.StartsWith(TermsrvPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[TermsrvPrefix.Length..];
        else if (value.StartsWith("TERMSRV\\", StringComparison.OrdinalIgnoreCase))
            value = value["TERMSRV\\".Length..];

        value = value.Trim().TrimStart('/', '\\');
        value = StripPort(value).Trim();

        return value.Length == 0 ? null : TermsrvPrefix + value;
    }

    private static string StripPort(string host)
    {
        if (host.Length == 0) return host;

        // Bracketed IPv6: [fe80::1]:3389 -> [fe80::1]
        if (host[0] == '[')
        {
            var close = host.IndexOf(']');
            if (close > 0 && close + 1 < host.Length && host[close + 1] == ':')
                return host[..(close + 1)];
            return host;
        }

        var colon = host.LastIndexOf(':');
        if (colon <= 0 || colon != host.IndexOf(':')) return host; // no port, or a bare IPv6 literal
        if (colon == host.Length - 1) return host[..colon];

        for (var i = colon + 1; i < host.Length; i++)
        {
            if (!char.IsAsciiDigit(host[i])) return host;
        }

        return host[..colon];
    }

    private static string ResolveCmdKeyPath()
    {
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (system32.Length != 0)
            {
                var full = Path.Combine(system32, "cmdkey.exe");
                if (File.Exists(full)) return full;
            }
        }
        catch
        {
            // Fall through to the bare name.
        }

        // The absolute path is preferred so nothing planted earlier in PATH can be handed a password.
        return "cmdkey.exe";
    }

    private static string TypeName(uint type)
    {
        if (type == CredentialApi.CRED_TYPE_GENERIC) return "Generic";
        if (type == CredentialApi.CRED_TYPE_DOMAIN_PASSWORD) return "Domain password";
        if (type == 3) return "Domain certificate";
        if (type == 4) return "Domain visible password";
        if (type == 5) return "Generic certificate";
        if (type == 6) return "Domain extended";
        return $"Type {type}";
    }

    private static string PersistName(uint persist)
    {
        if (persist == CredentialApi.CRED_PERSIST_SESSION) return "Session";
        if (persist == CredentialApi.CRED_PERSIST_LOCAL_MACHINE) return "Local machine";
        if (persist == CredentialApi.CRED_PERSIST_ENTERPRISE) return "Enterprise";
        return "Unknown";
    }
}
