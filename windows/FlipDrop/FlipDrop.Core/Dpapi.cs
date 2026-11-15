using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FlipDrop.Core;

/// <summary>
/// CurrentUser DPAPI via direct crypt32 P/Invoke, so no NuGet
/// (System.Security.Cryptography.ProtectedData) package is required.
/// Used to protect the certificate PFX and the token-hash store at rest.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Protect(byte[] data)
    {
        if (data.Length == 0) return [];
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = pin.AddrOfPinnedObject() };
            // No CRYPTPROTECT_LOCAL_MACHINE flag => CurrentUser scope.
            if (!CryptProtectData(ref inBlob, "FlipDrop", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    public static byte[] Unprotect(byte[] data)
    {
        if (data.Length == 0) return [];
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = pin.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
