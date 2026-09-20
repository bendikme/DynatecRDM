using System.Runtime.InteropServices;

namespace DynatecRDM.Interop;

/// <summary>
/// DPAPI (CryptProtectData/CryptUnprotectData). A failed call yields null rather than an
/// exception - callers treat a missing secret as "prompt the user", never as a crash.
/// </summary>
internal static unsafe class Crypt32
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(DATA_BLOB* pDataIn, string? szDataDescr, DATA_BLOB* pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, DATA_BLOB* pDataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(DATA_BLOB* pDataIn, IntPtr ppszDataDescr, DATA_BLOB* pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, DATA_BLOB* pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[]? Protect(byte[] plain, byte[]? entropy, bool machineScope = false)
    {
        if (plain is null || plain.Length == 0) return null;

        uint flags = CRYPTPROTECT_UI_FORBIDDEN | (machineScope ? CRYPTPROTECT_LOCAL_MACHINE : 0u);
        return Transform(plain, entropy, flags, protect: true);
    }

    public static byte[]? Unprotect(byte[] cipher, byte[]? entropy)
    {
        if (cipher is null || cipher.Length == 0) return null;

        return Transform(cipher, entropy, CRYPTPROTECT_UI_FORBIDDEN, protect: false);
    }

    private static byte[]? Transform(byte[] input, byte[]? entropy, uint flags, bool protect)
    {
        DATA_BLOB output = default;
        try
        {
            fixed (byte* pInput = input)
            fixed (byte* pEntropy = entropy)
            {
                var inputBlob = new DATA_BLOB { cbData = (uint)input.Length, pbData = (IntPtr)pInput };
                var entropyBlob = new DATA_BLOB
                {
                    cbData = entropy is null ? 0u : (uint)entropy.Length,
                    pbData = (IntPtr)pEntropy,
                };

                DATA_BLOB* pEntropyBlob = null;
                if (entropy is not null && entropy.Length != 0) pEntropyBlob = &entropyBlob;

                bool ok = protect
                    ? CryptProtectData(&inputBlob, null, pEntropyBlob, IntPtr.Zero, IntPtr.Zero, flags, &output)
                    : CryptUnprotectData(&inputBlob, IntPtr.Zero, pEntropyBlob, IntPtr.Zero, IntPtr.Zero, flags, &output);

                if (!ok || output.pbData == IntPtr.Zero || output.cbData == 0) return null;

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return result;
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                // The unprotected buffer holds plain text; scrub it before handing it back to Windows.
                new Span<byte>((void*)output.pbData, (int)output.cbData).Clear();
                LocalFree(output.pbData);
            }
        }
    }
}
