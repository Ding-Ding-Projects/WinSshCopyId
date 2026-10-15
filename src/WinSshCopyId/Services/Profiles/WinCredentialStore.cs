using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinSshCopyId.Services;

public interface ICredentialStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}

/// <summary>Windows Credential Manager generic credentials scoped to this application.</summary>
public sealed class WinCredentialStore : ICredentialStore
{
    private const uint Generic = 1;
    private const int NotFound = 1168;
    private static string Target(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 512 || key.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new ArgumentException("Invalid credential key.", nameof(key));
        return "WinSshCopyId/" + key;
    }
    public string? Get(string key)
    {
        if (!CredRead(Target(key), Generic, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Credential could not be read.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 2560 || credential.BlobSize % 2 != 0) throw new InvalidOperationException("Invalid credential data.");
            return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    }
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > 2560) throw new ArgumentException("Credential is too large.", nameof(value));
        IntPtr blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = Generic, TargetName = Target(key), BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2, UserName = "WinSshCopyId" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential could not be saved.");
        }
        finally
        {
            Array.Clear(bytes);
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }
    public void Delete(string key)
    {
        if (!CredDelete(Target(key), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential could not be removed.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
