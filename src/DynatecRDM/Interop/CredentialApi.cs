using System.Runtime.InteropServices;
using System.Text;

namespace DynatecRDM.Interop;

/// <summary>
/// Windows Credential Vault access (the API behind cmdkey). Passwords are handed to the
/// vault through unmanaged memory that is wiped and freed before returning.
/// </summary>
internal static class CredentialApi
{
    public const uint CRED_TYPE_GENERIC = 1;
    public const uint CRED_TYPE_DOMAIN_PASSWORD = 2;

    public const uint CRED_PERSIST_SESSION = 1;
    public const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    public const uint CRED_PERSIST_ENTERPRISE = 3;

    public sealed record CredentialEntry(
        string TargetName,
        string? UserName,
        uint Type,
        uint Persist,
        DateTime? LastWrittenUtc);

    private const uint CRED_ENUMERATE_ALL_CREDENTIALS = 0x1;
    private const int ERROR_NOT_FOUND = 1168;
    private const int MaxEnumerated = 65536;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>Creates or replaces a vault entry. The password never touches a command line.</summary>
    public static bool Write(string targetName, string userName, string password, uint type, uint persist)
    {
        if (string.IsNullOrEmpty(targetName)) return false;

        byte[] blob = Encoding.Unicode.GetBytes(password ?? string.Empty);
        IntPtr blobPtr = IntPtr.Zero;
        IntPtr targetPtr = IntPtr.Zero;
        IntPtr userPtr = IntPtr.Zero;
        try
        {
            blobPtr = Marshal.AllocHGlobal(blob.Length == 0 ? 1 : blob.Length);
            if (blob.Length != 0) Marshal.Copy(blob, 0, blobPtr, blob.Length);

            targetPtr = Marshal.StringToCoTaskMemUni(targetName);
            userPtr = string.IsNullOrEmpty(userName) ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(userName);

            var credential = new CREDENTIAL
            {
                Flags = 0,
                Type = type,
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                LastWritten = default,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = persist,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userPtr,
            };

            bool ok = CredWrite(ref credential, 0);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                Services.AppLog.Debug_(
                    $"CredWrite('{targetName}', type={type}, persist={persist}) failed with Win32 error {error}.");
            }
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                Wipe(blobPtr, blob.Length);
                Marshal.FreeHGlobal(blobPtr);
            }
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            Array.Clear(blob);
        }
    }

    /// <summary>Metadata only - the stored password is deliberately never returned.</summary>
    public static CredentialEntry? Read(string targetName, uint type)
    {
        if (string.IsNullOrEmpty(targetName)) return null;

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!CredRead(targetName, type, 0, out handle) || handle == IntPtr.Zero) return null;
            return Map(handle);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CredFree(handle);
        }
    }

    public static bool Delete(string targetName, uint type)
    {
        if (string.IsNullOrEmpty(targetName)) return false;
        try
        {
            return CredDelete(targetName, type, 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Entries matching a filter such as "TERMSRV/*"; null enumerates the whole vault.</summary>
    public static IReadOnlyList<CredentialEntry> Enumerate(string? filter)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            // The all-credentials flag is only legal together with a null filter.
            uint flags = filter is null ? CRED_ENUMERATE_ALL_CREDENTIALS : 0u;
            if (!CredEnumerate(filter, flags, out uint count, out handle) || handle == IntPtr.Zero || count == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0 && error != ERROR_NOT_FOUND)
                    Services.AppLog.Warn($"CredEnumerate('{filter ?? "*"}') failed with Win32 error {error}.");
                return Array.Empty<CredentialEntry>();
            }

            int total = count > MaxEnumerated ? MaxEnumerated : (int)count;
            var entries = new List<CredentialEntry>(total);
            for (int i = 0; i < total; i++)
            {
                IntPtr item = Marshal.ReadIntPtr(handle, i * IntPtr.Size);
                if (item == IntPtr.Zero) continue;

                var entry = Map(item);
                if (entry is not null) entries.Add(entry);
            }
            return entries;
        }
        catch (Exception)
        {
            return Array.Empty<CredentialEntry>();
        }
        finally
        {
            if (handle != IntPtr.Zero) CredFree(handle);
        }
    }

    private static CredentialEntry? Map(IntPtr pointer)
    {
        var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);

        string target = credential.TargetName != IntPtr.Zero
            ? Marshal.PtrToStringUni(credential.TargetName) ?? string.Empty
            : string.Empty;
        if (target.Length == 0) return null;

        string? user = credential.UserName != IntPtr.Zero ? Marshal.PtrToStringUni(credential.UserName) : null;
        if (string.IsNullOrEmpty(user)) user = null;

        return new CredentialEntry(target, user, credential.Type, credential.Persist, ToDateTime(credential.LastWritten));
    }

    private static DateTime? ToDateTime(FILETIME value)
    {
        long ticks = ((long)value.dwHighDateTime << 32) | value.dwLowDateTime;
        if (ticks <= 0) return null;
        try
        {
            return DateTime.FromFileTimeUtc(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void Wipe(IntPtr buffer, int length)
    {
        for (int i = 0; i < length; i++) Marshal.WriteByte(buffer, i, 0);
    }
}
