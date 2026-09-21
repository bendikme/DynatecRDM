using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace DynatecRDM.Services;

/// <summary>
/// Signs the .rdp files this application generates, and registers it as the publisher.
///
/// An UNSIGNED .rdp file produces Remote Desktop's harshest warning - "the publisher of this
/// remote connection cannot be identified" - with no way to accept it permanently. A SIGNED file
/// produces a different dialog: it names the publisher and offers "Remember my choices for remote
/// connections from this publisher". That answer is recorded per certificate, under
/// HKCU\...\Terminal Server Client\PublisherPermissions, so accepting it once covers every
/// connection this application starts, including those whose settings force a file.
///
/// The certificate is self-signed, created per user, with a non-exportable private key. It goes in
/// the current user's Trusted Publishers and Trusted Root stores so Windows can name the publisher;
/// both are per-user and need no administrator rights. Setting this up is an explicit action,
/// because trusting a signing certificate is the user's decision.
///
/// For a fleet, an administrator can instead list the thumbprint in the machine policy for trusted
/// .rdp publishers, which removes the dialog outright. The trust-publisher.ps1 script does that.
/// </summary>
public static class RdpSigning
{
    private const string Subject = "CN=DYNATEC Remote Desktop Manager";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string PolicyValue = "TrustedCertThumbprints";

    private static readonly string RdpSignPath =
        Path.Combine(Environment.SystemDirectory, "rdpsign.exe");

    /// <summary>The signing certificate's thumbprint, or null when none has been created yet.</summary>
    public static string? Thumbprint => FindCertificate()?.Thumbprint;

    /// <summary>True when an administrator has listed our certificate as a trusted .rdp publisher.</summary>
    public static bool IsTrustedByPolicy()
    {
        var mine = Thumbprint;
        if (string.IsNullOrEmpty(mine)) return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (key?.GetValue(PolicyValue) is not string listed) return false;

            foreach (var part in listed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(part.Trim(), mine, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reading the trusted-publisher policy failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the existing signing certificate, creating one in the current user's personal store
    /// if there is none. The private key is not exportable and never leaves this machine.
    /// </summary>
    public static X509Certificate2? EnsureCertificate()
    {
        var existing = FindCertificate();
        if (existing is not null) return existing;

        try
        {
            // PowerShell's New-SelfSignedCertificate is used rather than hand-building the
            // certificate: it produces exactly the code-signing certificate rdpsign expects, and
            // keeps the key non-exportable without any extra work.
            var script =
                "$c = New-SelfSignedCertificate -Type CodeSigningCert " +
                $"-Subject '{Subject}' -CertStoreLocation Cert:\\CurrentUser\\My " +
                "-KeyUsage DigitalSignature -KeyExportPolicy NonExportable " +
                "-NotAfter (Get-Date).AddYears(10); $c.Thumbprint";

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null) return null;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch { }
                AppLog.Warn("Creating the .rdp signing certificate timed out.");
                return null;
            }

            var created = FindCertificate();
            if (created is not null)
                AppLog.Info($"Created the .rdp signing certificate {created.Thumbprint}.");
            return created;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not create the .rdp signing certificate.", ex);
            return null;
        }
    }

    /// <summary>
    /// True once the certificate exists and Windows recognises it as a publisher, which is what
    /// makes Remote Desktop offer "Remember my choices for remote connections from this publisher".
    /// </summary>
    public static bool IsSigningReady()
    {
        var cert = FindCertificate();
        return cert is not null && InStore(cert, StoreName.TrustedPublisher) && InStore(cert, StoreName.Root);
    }

    /// <summary>
    /// Creates the signing certificate if needed and trusts it for the current user, so signed
    /// files name this application as the publisher. Per-user only: nothing machine-wide is
    /// touched and no administrator rights are used. Returns false if any step failed.
    /// </summary>
    public static bool EnableSigning()
    {
        var cert = EnsureCertificate();
        if (cert is null) return false;

        var ok = true;
        foreach (var store in new[] { StoreName.TrustedPublisher, StoreName.Root })
        {
            if (InStore(cert, store)) continue;
            if (!AddToStore(cert, store)) ok = false;
        }

        if (ok) AppLog.Info($"Signing enabled; {cert.Thumbprint} is trusted for this user.");
        return ok;
    }

    /// <summary>Removes the certificate and the trust again.</summary>
    public static bool DisableSigning()
    {
        var cert = FindCertificate();
        if (cert is null) return true;

        var ok = true;
        foreach (var store in new[] { StoreName.TrustedPublisher, StoreName.Root, StoreName.My })
            if (!RemoveFromStore(cert, store)) ok = false;

        AppLog.Info("Signing disabled and the certificate removed.");
        return ok;
    }

    private static bool InStore(X509Certificate2 cert, StoreName name)
    {
        try
        {
            using var store = new X509Store(name, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            foreach (var c in store.Certificates)
                if (string.Equals(c.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Checking the {name} store failed: {ex.Message}");
            return false;
        }
    }

    private static bool AddToStore(X509Certificate2 cert, StoreName name)
    {
        // certutil is used rather than X509Store.Add: adding to the user's Root store through the
        // managed API raises a confirmation dialog that cannot be shown from a background thread,
        // and fails outright when no interactive desktop is available.
        var path = Path.Combine(Path.GetTempPath(), $"dynatec-rdp-{Guid.NewGuid():N}.cer");
        try
        {
            File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));

            var psi = new ProcessStartInfo("certutil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-addstore");
            psi.ArgumentList.Add("-user");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(name == StoreName.TrustedPublisher ? "TrustedPublisher" : "Root");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not trust the certificate in {name}.", ex);
            return false;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static bool RemoveFromStore(X509Certificate2 cert, StoreName name)
    {
        try
        {
            var psi = new ProcessStartInfo("certutil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-delstore");
            psi.ArgumentList.Add("-user");
            psi.ArgumentList.Add(name switch
            {
                StoreName.TrustedPublisher => "TrustedPublisher",
                StoreName.Root => "Root",
                _ => "My",
            });
            psi.ArgumentList.Add(cert.Thumbprint);

            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not remove the certificate from {name}.", ex);
            return false;
        }
    }

    /// <summary>
    /// Signs the file in place. Returns false when there is no certificate or rdpsign fails; the
    /// caller simply carries on with an unsigned file, which still connects.
    /// </summary>
    public static bool Sign(string rdpPath)
    {
        if (string.IsNullOrWhiteSpace(rdpPath) || !File.Exists(rdpPath)) return false;
        if (!File.Exists(RdpSignPath)) return false;

        var cert = EnsureCertificate();
        if (cert is null) return false;

        try
        {
            var psi = new ProcessStartInfo(RdpSignPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            psi.ArgumentList.Add("/sha256");
            psi.ArgumentList.Add(cert.Thumbprint);
            psi.ArgumentList.Add(rdpPath);

            using var process = Process.Start(psi);
            if (process is null) return false;

            process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(true); } catch { }
                AppLog.Warn("rdpsign did not finish in time.");
                return false;
            }

            if (process.ExitCode == 0) return true;

            AppLog.Warn($"rdpsign failed with exit code {process.ExitCode}. {error.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Signing the .rdp file failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Thumbprint of a certificate to prefer over the self-signed one, set from the settings. A
    /// certificate from a trusted authority is what lets Windows name the publisher.
    /// </summary>
    public static string? PreferredThumbprint { get; set; }

    private static X509Certificate2? FindCertificate()
    {
        // The machine store first: the installer's publisher-trust step puts one certificate there,
        // trusted for every account, and lists exactly its thumbprint in the machine policy - so a
        // file signed with it opens without a prompt. A per-user certificate (the fallback when
        // trust was never set up) is only used when there is no machine one. Only a certificate
        // whose private key this process can actually sign with is accepted.
        return FindIn(StoreLocation.LocalMachine) ?? FindIn(StoreLocation.CurrentUser);
    }

    private static X509Certificate2? FindIn(StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);

            var wanted = PreferredThumbprint?.Replace(" ", string.Empty).Trim();
            if (!string.IsNullOrEmpty(wanted))
            {
                foreach (var candidate in store.Certificates)
                    if (string.Equals(candidate.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase) && Usable(candidate))
                        return candidate;
            }

            X509Certificate2? best = null;
            foreach (var candidate in store.Certificates)
            {
                if (!string.Equals(candidate.Subject, Subject, StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate.NotAfter <= DateTime.Now) continue;
                if (!Usable(candidate)) continue;
                if (best is null || candidate.NotAfter > best.NotAfter) best = candidate;
            }

            return best;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Looking up the signing certificate in {location} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when the certificate has a private key this process may sign with.</summary>
    private static bool Usable(X509Certificate2 cert)
    {
        try { return cert.HasPrivateKey; }
        catch { return false; }
    }
}
