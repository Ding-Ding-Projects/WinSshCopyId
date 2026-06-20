using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinSshCopyId.Services;

/// <summary>
/// Thin wrapper over the Windows Data Protection API (DPAPI) using crypt32.dll
/// directly, so we do not need an extra NuGet package. Protected blobs can only
/// be decrypted by the same Windows user on the same machine.
/// </summary>
public static class Dpapi
{
    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plaintext)
    {
        byte[] input = Encoding.UTF8.GetBytes(plaintext);
        DataBlob inBlob = default;
        DataBlob outBlob = default;
        try
        {
            inBlob.pbData = Marshal.AllocHGlobal(input.Length);
            inBlob.cbData = input.Length;
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);

            if (!CryptProtectData(ref inBlob, "WinSshCopyId", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, ref outBlob))
            {
                throw new InvalidOperationException("DPAPI Protect failed: " + Marshal.GetLastWin32Error());
            }

            byte[] encrypted = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero)
            {
                Array.Clear(input, 0, input.Length);
                Marshal.FreeHGlobal(inBlob.pbData);
            }
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    /// <summary>Returns the decrypted text, or null if the blob is invalid or not ours.</summary>
    public static string? Unprotect(string protectedBase64)
    {
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedBase64);
        }
        catch
        {
            return null;
        }

        DataBlob inBlob = default;
        DataBlob outBlob = default;
        try
        {
            inBlob.pbData = Marshal.AllocHGlobal(encrypted.Length);
            inBlob.cbData = encrypted.Length;
            Marshal.Copy(encrypted, 0, inBlob.pbData, encrypted.Length);

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, ref outBlob))
            {
                return null;
            }

            byte[] plain = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
            string result = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inBlob.pbData);
            }
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }
}
