using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinSshCopyId.Tools.Ollama;

/// <summary>Windows native Authenticode verification with cached trust data only and an exact reviewed signer pin.</summary>
public static class OllamaPublisherVerifier
{
    public static Task<bool> VerifyAsync(string path, string expectedSubject, string expectedThumbprint,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(expectedSubject) ||
            expectedThumbprint.Length != 40 || !expectedThumbprint.All(Uri.IsHexDigit)) return Task.FromResult(false);
        return Task.Run(() => Verify(path, expectedSubject, expectedThumbprint, token), token);
    }
    private static bool Verify(string path, string subject, string thumbprint, CancellationToken token)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UIChoice = 2, UnionChoice = 1,
            File = filePointer, StateAction = 1, ProviderFlags = 0x1000 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            token.ThrowIfCancellationRequested();
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            token.ThrowIfCancellationRequested();
            return certificate.Subject.Equals(subject, StringComparison.Ordinal) &&
                certificate.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(filePointer); Marshal.FreeHGlobal(filePointer);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
