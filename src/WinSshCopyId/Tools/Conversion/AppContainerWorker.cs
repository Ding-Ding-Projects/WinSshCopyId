using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WinSshCopyId.Tools.Conversion;

public sealed record ConfinedWorkerRequest(string StagingDirectory, string AdapterId,
    IReadOnlyDictionary<string, string> PackageMemberSha256);

public sealed class ConfinedWorkerReceipt
{
    internal ConfinedWorkerReceipt(string adapterId, string executableSha256, string outputSha256)
    { AdapterId = adapterId; ExecutableSha256 = executableSha256; OutputSha256 = outputSha256; ExecutedAt = DateTimeOffset.UtcNow; }
    public string AdapterId { get; }
    public string ExecutableSha256 { get; }
    public string OutputSha256 { get; }
    public DateTimeOffset ExecutedAt { get; }
}

/// <summary>Windows-only capability-free worker launch. Staging is private, dedicated and caller-owned.</summary>
public static class AppContainerWorker
{
    // Input is staging/input.bin; output is staging/output.bin. The package is
    // staging/package, containing an entire self-contained published worker.
    public static async Task<ConfinedWorkerReceipt> ExecuteAsync(ConfinedWorkerRequest request, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var adapter = ConversionCatalog.Adapters.SingleOrDefault(x => x.Id == request.AdapterId);
        if (adapter is null || adapter.Sources.IsEmpty) throw new NotSupportedException("Managed adapter is not implemented.");
        var stage = Path.GetFullPath(request.StagingDirectory);
        var ownedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSshCopyId", "ConversionSandbox");
        if (!string.Equals(Path.GetDirectoryName(stage), ownedRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw new IOException("Confinement requires a dedicated application-owned GUID staging directory.");
        var package = Path.Combine(stage, "package");
        var executable = Path.Combine(package, "WinSshCopyId.ToolWorker.exe");
        var input = Path.Combine(stage, "input.bin");
        var output = Path.Combine(stage, "output.bin");
        RejectReparseAncestors(stage);
        if (File.Exists(output)) throw new IOException("Staging output must not already exist.");
        if (new FileInfo(input).Length > LocalConverter.MaximumInputBytes) throw new InvalidDataException("Staged input exceeds its limit.");
        var members = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Take(4097).ToArray();
        if (members.Length != request.PackageMemberSha256.Count || members.Length > 4096) throw new InvalidDataException("Worker package inventory mismatch.");
        foreach (var member in members)
        {
            RejectReparseAncestors(member);
            var relative = Path.GetRelativePath(package, member).Replace('\\', '/');
            if (!request.PackageMemberSha256.TryGetValue(relative, out var expected) || !await MatchesAsync(member, expected, token))
                throw new InvalidDataException("Worker package member integrity mismatch.");
        }
        if (!request.PackageMemberSha256.TryGetValue("WinSshCopyId.ToolWorker.exe", out var executableHash))
            throw new InvalidDataException("Worker executable is not in the package manifest.");
        var profile = "WinSshCopyId.Conversion." + Guid.NewGuid().ToString("N");
        IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, capabilities = IntPtr.Zero, job = IntPtr.Zero, environment = IntPtr.Zero,
            handles = IntPtr.Zero, nullInput = IntPtr.Zero;
        FileStream? diagnostics = null;
        PROCESS_INFORMATION process = default;
        bool createdProfile = false;
        try
        {
            var hr = CreateAppContainerProfile(profile, profile, "Offline conversion worker", IntPtr.Zero, 0, out sid);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            createdProfile = true;
            // Profile SID is independently derived and compared, rather than accepting an arbitrary caller SID.
            Check(DeriveAppContainerSidFromAppContainerName(profile, out var derived) >= 0);
            try { Check(EqualSid(sid, derived)); } finally { FreeSid(derived); }
            Check(ConvertSidToStringSidW(sid, out var sidTextPointer));
            string sidText;
            try { sidText = Marshal.PtrToStringUni(sidTextPointer)!; } finally { LocalFree(sidTextPointer); }
            // Protected DACLs grant the current owner full control and this capability SID only.
            // No user-selected source or final destination ACL is changed.
            ApplyAcl(stage, $"D:P(A;OICI;FA;;;OW)(A;;0x1200a9;;;{sidText})");
            ApplyAcl(package, $"D:P(A;OICI;FA;;;OW)(A;OICI;0x1200a9;;;{sidText})");
            foreach (var directory in Directory.EnumerateDirectories(package, "*", SearchOption.AllDirectories))
                ApplyAcl(directory, $"D:P(A;OICI;FA;;;OW)(A;OICI;0x1200a9;;;{sidText})");
            foreach (var member in members) ApplyAcl(member, $"D:P(A;;FA;;;OW)(A;;0x1200a9;;;{sidText})");
            ApplyAcl(input, $"D:P(A;;FA;;;OW)(A;;FR;;;{sidText})");
            // Output gets its own directory, avoiding write access to worker code or source bytes.
            var outputDirectory = Path.Combine(stage, "result");
            if (Directory.Exists(outputDirectory)) throw new IOException("Result directory must be newly created.");
            Directory.CreateDirectory(outputDirectory);
            ApplyAcl(outputDirectory, $"D:P(A;OICI;FA;;;OW)(A;OICI;0x1301bf;;;{sidText})S:(ML;OICI;NW;;;LW)", true);
            output = Path.Combine(outputDirectory, "output.bin");
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
            Marshal.StructureToPtr(new SECURITY_CAPABILITIES { AppContainerSid = sid, CapabilityCount = 0, Capabilities = IntPtr.Zero }, capabilities, false);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, capabilities,
                (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(), IntPtr.Zero, IntPtr.Zero));
            var diagnosticPath = Path.Combine(outputDirectory, "worker-diagnostics.txt");
            diagnostics = new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            var diagnosticHandle = diagnostics.SafeFileHandle.DangerousGetHandle();
            Check(SetHandleInformation(diagnosticHandle, 1, 1));
            nullInput = CreateFileW("NUL", 0x80000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            Check(nullInput != new IntPtr(-1)); Check(SetHandleInformation(nullInput, 1, 1));
            handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handles, diagnosticHandle); Marshal.WriteIntPtr(handles, IntPtr.Size, nullInput);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero));
            var startup = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>(),
                dwFlags = 0x100, hStdInput = nullInput, hStdOutput = diagnosticHandle, hStdError = diagnosticHandle }, lpAttributeList = attributes };
            job = CreateJobObjectW(IntPtr.Zero, null); Check(job != IntPtr.Zero);
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                { LimitFlags = 0x2000 | 0x100 | 0x8 | 0x2, ActiveProcessLimit = 1, PerProcessUserTimeLimit = 30L * 10000000 },
                ProcessMemoryLimit = (UIntPtr)(256UL * 1024 * 1024)
            };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
            // Do not inherit credentials or unrelated runtime configuration through the parent's environment.
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(systemRoot)) throw new IOException("Windows directory is unavailable.");
            var environmentValues = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DOTNET_EnableDiagnostics"] = "0", ["DOTNET_MULTILEVEL_LOOKUP"] = "0", ["SystemRoot"] = systemRoot,
                ["TEMP"] = outputDirectory, ["TMP"] = outputDirectory, ["WINDIR"] = systemRoot,
                ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["ALLUSERSPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["SystemDrive"] = Path.GetPathRoot(systemRoot)!.TrimEnd('\\'),
                ["PATH"] = Environment.SystemDirectory
            };
            environment = Marshal.StringToHGlobalUni(string.Join('\0', environmentValues.Select(x => x.Key + "=" + x.Value)) + "\0\0");
            var command = new StringBuilder(Quote(executable) + " --verify-managed " + Quote(request.AdapterId) + " " + Quote(input) + " " + Quote(output));
            Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x80000 | 0x4 | 0x08000000 | 0x400,
                environment, package, ref startup, out process));
            Check(AssignProcessToJobObject(job, process.hProcess));
            Check(ResumeThread(process.hThread) != uint.MaxValue);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            while (WaitForSingleObject(process.hProcess, 0) == 0x102)
            { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(100, deadline.Token); }
            Check(GetExitCodeProcess(process.hProcess, out var exitCode));
            diagnostics.Dispose(); diagnostics = null;
            if (exitCode != 0)
            {
                var detail = new FileInfo(diagnosticPath).Length <= 4096 ? await File.ReadAllTextAsync(diagnosticPath, token) : "Diagnostics exceeded the bounded display limit.";
                throw new IOException("Confined worker exited with code " + exitCode + " (0x" + exitCode.ToString("X8") + "). " + detail.Trim());
            }
            RejectReparseAncestors(output);
            if (new FileInfo(output).Length > LocalConverter.MaximumOutputBytes) throw new InvalidDataException("Confined output exceeded its byte limit.");
            // The pinned worker validates its output before atomic promotion. The parent
            // hashes bounded output but never decodes or parses user content outside confinement.
            return new(request.AdapterId, executableHash, await HashAsync(output, token));
        }
        finally
        {
            // Closing a kill-on-close job kills only this launch. Wait for the exact process before freeing its profile.
            Exception? teardownFailure = null;
            if (job != IntPtr.Zero) CloseHandle(job);
            if (process.hProcess != IntPtr.Zero)
            {
                TerminateProcess(process.hProcess, 125);
                if (WaitForSingleObject(process.hProcess, 10000) != 0) teardownFailure = new IOException("Confined worker termination was not confirmed.");
                CloseHandle(process.hProcess);
            }
            if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            diagnostics?.Dispose();
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (nullInput != IntPtr.Zero && nullInput != new IntPtr(-1)) CloseHandle(nullInput);
            if (sid != IntPtr.Zero) FreeSid(sid);
            if (createdProfile)
            {
                var removal = DeleteAppContainerProfile(profile);
                if (removal < 0) teardownFailure = new IOException("Confined profile removal was not confirmed.", Marshal.GetExceptionForHR(removal));
            }
            if (teardownFailure is not null) throw teardownFailure;
        }
    }

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.EndsWith('\\')) throw new ArgumentException("Unsupported command argument shape.");
        return "\"" + value + "\"";
    }
    private static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Staging and package reparse points are not supported.");
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, token)); }
    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
        => expected.Length == 64 && expected.All(Uri.IsHexDigit) && (await HashAsync(path, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void ApplyAcl(string path, string sddl, bool lowIntegrity = false)
    {
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _));
        try
        {
            var information = 4u | (lowIntegrity ? 0x10u : 0);
            Check(SetFileSecurityW(path, information | 0x80000000, descriptor));
            GetFileSecurityW(path, information, IntPtr.Zero, 0, out var needed);
            var readback = Marshal.AllocHGlobal((int)needed);
            try
            {
                Check(GetFileSecurityW(path, information, readback, needed, out _));
                Check(GetSecurityDescriptorDacl(descriptor, out var expectedPresent, out var expectedAcl, out _));
                Check(GetSecurityDescriptorDacl(readback, out var actualPresent, out var actualAcl, out _));
                if (!expectedPresent || !actualPresent || expectedAcl == IntPtr.Zero || actualAcl == IntPtr.Zero)
                    throw new IOException("Staging ACL readback is missing its DACL.");
                var expectedSize = (ushort)Marshal.ReadInt16(expectedAcl, 2);
                var actualSize = (ushort)Marshal.ReadInt16(actualAcl, 2);
                var expectedBytes = new byte[expectedSize]; var actualBytes = new byte[actualSize];
                Marshal.Copy(expectedAcl, expectedBytes, 0, expectedSize); Marshal.Copy(actualAcl, actualBytes, 0, actualSize);
                if (!expectedBytes.AsSpan().SequenceEqual(actualBytes)) throw new IOException("Staging ACL readback mismatch.");
                if (lowIntegrity)
                {
                    Check(GetSecurityDescriptorSacl(descriptor, out var expectedLabelPresent, out var expectedLabel, out _));
                    Check(GetSecurityDescriptorSacl(readback, out var actualLabelPresent, out var actualLabel, out _));
                    if (!expectedLabelPresent || !actualLabelPresent || expectedLabel == IntPtr.Zero || actualLabel == IntPtr.Zero)
                        throw new IOException("Result integrity label is missing.");
                    var expectedLabelBytes = new byte[(ushort)Marshal.ReadInt16(expectedLabel, 2)];
                    var actualLabelBytes = new byte[(ushort)Marshal.ReadInt16(actualLabel, 2)];
                    Marshal.Copy(expectedLabel, expectedLabelBytes, 0, expectedLabelBytes.Length);
                    Marshal.Copy(actualLabel, actualLabelBytes, 0, actualLabelBytes.Length);
                    if (!expectedLabelBytes.AsSpan().SequenceEqual(actualLabelBytes)) throw new IOException("Result integrity-label readback mismatch.");
                }
            }
            finally { Marshal.FreeHGlobal(readback); }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    { public int cb; public IntPtr lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern bool EqualSid(IntPtr first, IntPtr second);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetFileSecurityW(string path, uint information, IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetFileSecurityW(string path, uint information, IntPtr descriptor, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int information, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
