using System.Security.Cryptography;
using System.Text;
using DynatecRDM.Interop;

namespace DynatecRDM.Services;

/// <summary>
/// DPAPI-backed secret protection. Secrets at rest are bound to the current user and to a fixed
/// application entropy. Other processes running as the same Windows user can still decrypt them;
/// DPAPI protects storage, not a compromised user account.
/// The .rdp payload deliberately does not use that entropy: mstsc decrypts it itself and only
/// understands its own convention.
/// </summary>
public sealed class SecretProtector : ISecretProtector
{
    private static readonly byte[] AppEntropy = SHA256.HashData(Encoding.ASCII.GetBytes("DynatecRDM.v1"));

    public byte[] Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return [];

        var plain = Encoding.UTF8.GetBytes(plainText);
        byte[]? cipher;
        try
        {
            cipher = Crypt32.Protect(plain, AppEntropy);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("DPAPI CryptProtectData failed while protecting a secret.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return cipher ?? throw new InvalidOperationException("DPAPI CryptProtectData failed while protecting a secret.");
    }

    public string? Unprotect(byte[] cipherText)
    {
        if (cipherText is null || cipherText.Length == 0) return null;

        byte[]? plain = null;
        try
        {
            plain = Crypt32.Unprotect(cipherText, AppEntropy);
            if (plain is null)
            {
                AppLog.Warn($"Unprotect failed for a {cipherText.Length}-byte blob (wrong user profile, or the blob is corrupt).");
                return null;
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Unprotect threw while decrypting a stored secret.", ex);
            return null;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string ProtectForRdpFile(string plainPassword)
    {
        if (string.IsNullOrEmpty(plainPassword)) return string.Empty;

        // mstsc expects the password as UTF-16LE with no trailing null, protected with no entropy.
        var plain = Encoding.Unicode.GetBytes(plainPassword);
        byte[]? cipher;
        try
        {
            cipher = Crypt32.Protect(plain, null);
        }
        catch (Exception ex)
        {
            AppLog.Error("DPAPI failed while building the .rdp password payload; the password line will be omitted.", ex);
            return string.Empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        if (cipher is null || cipher.Length == 0)
        {
            AppLog.Error("DPAPI returned no data for the .rdp password payload; the password line will be omitted.");
            return string.Empty;
        }

        return Convert.ToHexString(cipher);
    }
}
