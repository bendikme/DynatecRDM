using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace DynatecRDM.Services;

/// <summary>
/// Signs generated .rdp files so Remote Desktop stops warning about them.
///
/// Windows shows "the publisher of this remote connection cannot be identified" for every unsigned
/// .rdp file, every time, and a user cannot permanently accept it. Per-user trust does not work:
/// putting a self-signed certificate in the user's Trusted Publishers and Trusted Root stores, and
/// listing its thumbprint in PublisherBypassList, still leaves the warning in place. That is
/// deliberate - only an administrator may decide which publishers of .rdp files are trusted.
///
/// The one supported route is the machine policy "Specify SHA1 thumbprints of certificates
/// representing trusted .rdp publishers". Once our certificate's thumbprint is listed there, every
/// file this application signs opens silently, with every setting intact. build\trust-publisher.ps1
/// performs that step; it needs administrator rights and is run once per machine.
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

    private static X509Certificate2? FindCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            X509Certificate2? best = null;
            foreach (var candidate in store.Certificates)
            {
                if (!string.Equals(candidate.Subject, Subject, StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate.NotAfter <= DateTime.Now) continue;
                if (best is null || candidate.NotAfter > best.NotAfter) best = candidate;
            }

            return best;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Looking up the signing certificate failed: {ex.Message}");
            return null;
        }
    }
}
