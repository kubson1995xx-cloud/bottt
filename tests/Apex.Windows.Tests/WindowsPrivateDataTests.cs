using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Windows.Tests;

// Mutations caught: inherited creation, omitted recursive admission, null/foreign ACL
// admission, link-following/rename races, prefix-only containment, overbroad ancestor
// rejection, DeleteChild admission, package fallback, and guard-after-lock ordering.
[Trait("Gate", "Source")]
public sealed class WindowsPrivateDataTests
{
    [Fact]
    public void CreatesPrivateRootBeforeAnyFile()
    {
        using var f = new PrivateFixture();
        string path = System.IO.Path.Combine(f.Root, "new", "data");
        using var lease = WindowsPrivateData.Open(path, AppContext.BaseDirectory);
        Assert.True(lease.IsNew);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        foreach (string dir in new[] { System.IO.Path.GetDirectoryName(path)!, path })
        {
            var acl = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(PrivateFixture.Me, acl.GetOwner(typeof(SecurityIdentifier)));
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.Equal(PrivateFixture.Trusted.OrderBy(s => s.Value).Select(s => s.Value), rules.Select(r => r.IdentityReference.Value).OrderBy(s => s));
            Assert.All(rules, r => { Assert.Equal(AccessControlType.Allow, r.AccessControlType); Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights); Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags); });
        }
        File.WriteAllText(System.IO.Path.Combine(path, "inherited.txt"), "private");
        Assert.DoesNotContain(new FileInfo(System.IO.Path.Combine(path, "inherited.txt")).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => !PrivateFixture.Trusted.Contains(r.IdentityReference));
        lease.ValidateTree();
    }

    [Fact]
    public void ExistingBroadNestedFileRefusesUnchanged()
    {
        using var f = new PrivateFixture();
        string data = f.Directory("data"), nested = f.Directory("data/nested");
        string file = System.IO.Path.Combine(nested, "unchanged.json"); File.WriteAllText(file, "{\"preserve\":true}");
        var acl = new FileInfo(file).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(PrivateFixture.Everyone, FileSystemRights.Read, AccessControlType.Allow)); new FileInfo(file).SetAccessControl(acl);
        var before = f.Snapshot();
        var ex = Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(data, AppContext.BaseDirectory).Dispose());
        Assert.Equal("HOST_DATA_PERMISSIONS_INVALID", ex.Code);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullDaclAndForeignOwnerRefuse(bool foreignOwner)
    {
        using var f = new PrivateFixture(); string data = f.Directory("data");
        if (foreignOwner)
        {
            PrivateFixture.EnableRestorePrivilege();
            var acl = new DirectoryInfo(data).GetAccessControl(); acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)); new DirectoryInfo(data).SetAccessControl(acl);
            Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), new DirectoryInfo(data).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        }
        else
        {
            Assert.True(ConvertStringSecurityDescriptorToSecurityDescriptor("D:NO_ACCESS_CONTROL", 1, out var sd, out _));
            try { Assert.True(SetFileSecurity(data, 4, sd)); } finally { LocalFree(sd); }
            var raw = new RawSecurityDescriptor(new DirectoryInfo(data).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
            Assert.Null(raw.DiscretionaryAcl); // Fixture itself must really be null, not an empty DACL.
        }
        var before = f.Snapshot();
        var ex = Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(data, AppContext.BaseDirectory).Dispose());
        Assert.Equal("HOST_DATA_PERMISSIONS_INVALID", ex.Code); Assert.Equal(before, f.Snapshot());
        // Restore fixture ownership/ACL only after preservation assertions, solely for cleanup.
        PrivateFixture.Protect(data);
    }

    [Fact]
    public void ReparseLeafAncestorAndSwapRefuse()
    {
        using var f = new PrivateFixture(); string target = f.Directory("target");
        string link = System.IO.Path.Combine(f.Root, "junction"); PrivateFixture.Junction(link, target);
        try
        {
            var before = f.Snapshot();
            Assert.Equal("HOST_DATA_PATH_INVALID", Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(link, AppContext.BaseDirectory).Dispose()).Code);
            Assert.Equal(before, f.Snapshot());
            Assert.Equal("HOST_DATA_PATH_INVALID", Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(System.IO.Path.Combine(link, "child"), AppContext.BaseDirectory).Dispose()).Code);
            string data = f.Directory("data"), nested = System.IO.Path.Combine(data, "linked"); PrivateFixture.Junction(nested, target);
            try { var tree = f.Snapshot(); Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(data, AppContext.BaseDirectory).Dispose()); Assert.Equal(tree, f.Snapshot()); }
            finally { Directory.Delete(nested); }
            using var lease = WindowsPrivateData.Open(data, AppContext.BaseDirectory);
            Assert.Throws<IOException>(() => Directory.Move(data, data + "-swapped"));
            lease.ValidateTree();
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void PackageAliasContainmentAndSafeSibling()
    {
        using var f = new PrivateFixture(); string release = f.Directory("Long Release Package"), data = System.IO.Path.Combine(release, "data");
        Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(data, release).Dispose()); Assert.False(Directory.Exists(data));
        var b = new StringBuilder(32768); Assert.NotEqual(0u, GetShortPathName(release, b, b.Capacity));
        string alias = b.ToString();
        Assert.False(alias.Equals(release, StringComparison.OrdinalIgnoreCase), "Native 8.3 alias fixture unavailable; alias coverage cannot be claimed");
        Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(System.IO.Path.Combine(alias, "data"), release.ToUpperInvariant()).Dispose());
        string sibling = f.Directory("Long Release Package-Other"); using var lease = WindowsPrivateData.Open(sibling, release); Assert.False(lease.IsNew);
    }

    [Fact]
    public void TypicalSystemAncestorsAndInheritOnlyAcesWork()
    {
        using var f = new PrivateFixture();
        string ancestor = f.Directory("ancestor");
        var acl = new DirectoryInfo(ancestor).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(PrivateFixture.Everyone, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        // Object-effective creation of unrelated siblings is not replacement of our child.
        acl.AddAccessRule(new FileSystemAccessRule(PrivateFixture.Everyone, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles, AccessControlType.Allow));
        new DirectoryInfo(ancestor).SetAccessControl(acl);
        string data = System.IO.Path.Combine(ancestor, "data");
        using var lease = WindowsPrivateData.Open(data, AppContext.BaseDirectory); lease.ValidateTree();
        Assert.True(new DirectoryInfo(data).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void UnsafeDeleteChildAncestorRefuses()
    {
        using var f = new PrivateFixture(); string ancestor = f.Directory("ancestor"), data = f.Directory("ancestor/data");
        var acl = new DirectoryInfo(ancestor).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(PrivateFixture.Everyone, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow)); new DirectoryInfo(ancestor).SetAccessControl(acl);
        var before = f.Snapshot();
        Assert.Equal("HOST_DATA_PERMISSIONS_INVALID", Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(data, AppContext.BaseDirectory).Dispose()).Code);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void MissingLocalAppDataHasNoPackageFallback()
    {
        Assert.True(OperatingSystem.IsWindows(), "Native Windows gate cannot pass on another OS");
        Assert.Equal("HOST_DATA_UNAVAILABLE", Assert.Throws<WindowsDataException>(() => ConfigStore.DefaultDataDirFromLocalAppData("")).Code);
    }

    [Fact]
    public async Task ProductionEntryRefusesBeforeLockOrConfigMutation()
    {
        using var f = new PrivateFixture(); string data = f.Directory("data"), nested = f.Directory("data/nested");
        File.WriteAllText(System.IO.Path.Combine(data, "apex.config.json"), "{"); // RED safely exits old host before listeners/accounts.
        string file = System.IO.Path.Combine(nested, "private.txt"); File.WriteAllText(file, "unchanged");
        var acl = new FileInfo(file).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(PrivateFixture.Everyone, FileSystemRights.Read, AccessControlType.Allow)); new FileInfo(file).SetAccessControl(acl);
        var before = f.Snapshot();
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(typeof(Apex.Desktop.Program).Assembly.Location); psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(data); psi.ArgumentList.Add("--no-browser");
        using var process = Process.Start(psi)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } finally { if (!process.HasExited) process.Kill(true); }
        string output = await stdout + await stderr;
        Assert.Equal(2, process.ExitCode); Assert.Equal(before, f.Snapshot());
        Assert.Contains("HOST_DATA_PERMISSIONS_INVALID", output); Assert.DoesNotContain("Exception", output);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("traversal")]
    [InlineData("ads")]
    [InlineData("device")]
    [InlineData("unc")]
    [InlineData("source")]
    public void InvalidPathsRefuseWithoutCreation(string kind)
    {
        using var f = new PrivateFixture();
        string path = kind switch {
            // GetRelativePath across runner D: and profile C: returns an absolute
            // path, so use an unambiguously relative, unique path instead.
            "relative" => ".\\apex-relative-" + Guid.NewGuid().ToString("N"),
            "traversal" => System.IO.Path.Combine(f.Root, "parent", "..", "data"),
            "ads" => System.IO.Path.Combine(f.Root, "data:stream"),
            "device" => "\\\\?\\" + System.IO.Path.Combine(f.Root, "data"),
            "unc" => "\\\\localhost\\" + f.Root[0] + "$" + f.Root[2..] + "\\data",
            _ => System.IO.Path.Combine(f.Directory("source"), "data") };
        if (kind == "source") File.WriteAllText(System.IO.Path.Combine(f.Root, "source", "APEX.Trading.Demo.sln"), "marker");
        var before = f.Snapshot();
        Assert.Equal("HOST_DATA_PATH_INVALID", Assert.Throws<WindowsDataException>(() => WindowsPrivateData.Open(path, AppContext.BaseDirectory).Dispose()).Code);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public async Task UnavailablePrivateRootExitsWithSafeCodeWithoutWrites()
    {
        using var f = new PrivateFixture(); string data = f.Directory("data");
        File.WriteAllText(System.IO.Path.Combine(data, "apex.config.json"), "{");
        var acl = new DirectoryInfo(data).GetAccessControl();
        // Explicit current-user denial also applies to an elevated runner; an
        // Administrators allow must not hide the unavailable-directory case.
        acl.AddAccessRule(new FileSystemAccessRule(PrivateFixture.Me, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
        new DirectoryInfo(data).SetAccessControl(acl);
        var before = f.Snapshot();
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(typeof(Apex.Desktop.Program).Assembly.Location); psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(data); psi.ArgumentList.Add("--no-browser");
        using var process = Process.Start(psi)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } finally { if (!process.HasExited) process.Kill(true); }
        string output = await stdout + await stderr;
        Assert.Equal(2, process.ExitCode); Assert.Equal(before, f.Snapshot());
        Assert.Contains("HOST_DATA_UNAVAILABLE", output); Assert.DoesNotContain("Exception", output);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string s, uint revision, out IntPtr sd, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFileSecurityW")] static extern bool SetFileSecurity(string path, uint info, IntPtr sd);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")] static extern uint GetShortPathName(string path, StringBuilder output, int length);
}

internal sealed class PrivateFixture : IDisposable
{
    internal static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;
    internal static SecurityIdentifier Everyone => new(WellKnownSidType.WorldSid, null);
    internal static SecurityIdentifier[] Trusted => new[] { Me, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
    internal string Root { get; }
    internal PrivateFixture()
    {
        Assert.True(OperatingSystem.IsWindows(), "Native Windows gate cannot pass on another OS");
        // KnownFolder, not runner TEMP (which can grant replace-child rights).
        Root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexTask1Tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Root); Protect(Root);
    }
    internal string Directory(string relative) { string p = System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)); System.IO.Directory.CreateDirectory(p); Protect(p); return p; }
    internal static void Protect(string path)
    {
        var acl = new DirectorySecurity(); acl.SetOwner(Me); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in Trusted) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    internal string[] Snapshot()
    {
        var list = new List<string>();
        void Visit(string p)
        {
            var info = (File.GetAttributes(p) & FileAttributes.Directory) != 0 ? (FileSystemInfo)new DirectoryInfo(p) : new FileInfo(p);
            FileSystemSecurity acl = info is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)info).GetAccessControl();
            list.Add(System.IO.Path.GetRelativePath(Root, p) + "|" + Convert.ToBase64String(acl.GetSecurityDescriptorBinaryForm()) + "|" + (int)info.Attributes + "|" + (info is FileInfo ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : ""));
            if (info is DirectoryInfo dir && (info.Attributes & FileAttributes.ReparsePoint) == 0) foreach (var child in dir.EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.Ordinal)) Visit(child.FullName);
        }
        Visit(Root); return list.ToArray();
    }
    internal static void Junction(string link, string target)
    {
        // cmd is not a CommandLineToArgvW consumer: don't quote its whole /c
        // command as one ArgumentList item (that quotes mklink as a filename).
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!; string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); Assert.True(p.ExitCode == 0, "Junction fixture: " + output);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }
    public void Dispose() { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, true); }
    [StructLayout(LayoutKind.Sequential)] struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] struct Privileges { internal uint Count; internal Luid Luid; internal uint Attributes; }
    internal static void EnableRestorePrivilege()
    {
        Assert.True(OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var token));
        try { Assert.True(LookupPrivilegeValue(null, "SeRestorePrivilege", out var luid)); var p = new Privileges { Count = 1, Luid = luid, Attributes = 2 }; Assert.True(AdjustTokenPrivileges(token, false, ref p, 0, IntPtr.Zero, IntPtr.Zero)); Assert.Equal(0, Marshal.GetLastWin32Error()); }
        finally { CloseHandle(token); }
    }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref Privileges state, uint length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
