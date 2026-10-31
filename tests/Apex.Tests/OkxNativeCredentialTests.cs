using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("OKX composition"), Trait("Gate", "NativeWindows")]
public sealed class OkxNativeCredentialTests
{
    private static void NativePrerequisite()
    {
        Assert.True(OperatingSystem.IsWindows(), "Native Windows required; never silently skip");
        // Independent, already accepted CurrentUser DPAPI primitive proves OS API
        // availability before testing the new store; does not prove new scope entropy.
        var method = typeof(WindowsLocalAccess).GetMethod("Crypt", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] plain = RandomNumberGenerator.GetBytes(32), encrypted = (byte[])method.Invoke(null, new object[] { plain, true })!;
        byte[] opened = (byte[])method.Invoke(null, new object[] { encrypted, false })!;
        try { Assert.True(plain.AsSpan().SequenceEqual(opened)); }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(opened); }
    }
    private static SortedDictionary<string, string> Inventory(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            var info = (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            var security = info is DirectoryInfo dir ? (FileSystemSecurity)dir.GetAccessControl() : ((FileInfo)info).GetAccessControl();
            result[Path.GetRelativePath(root, path)] = security.GetSecurityDescriptorSddlForm(AccessControlSections.All) + (info is FileInfo ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "");
        }
        return result;
    }
    private static void Create(WindowsOkxCredentialStore store, OkxScopeBinding b, OkxScopePaths p, CredentialFixture f) => store.Create(b, p, b.Account.Environment == NavalEnvironment.OkxDemo ? "DemoTradingApi" : "LiveAccountApi", f.Key, f.Secret, f.Passphrase);
    [Fact]
    public async Task OkxCredentials_NativeDpapiAclAndCreateNew()
    {
        NativePrerequisite(); await using var h = new OkxCompositionFixture(); using var f = new CredentialFixture();
        var b = CredentialFixture.Binding(); var p = OkxConfiguration.Paths(h.Root, b); var store = new WindowsOkxCredentialStore(h.Root);
        var failure = Record.Exception(() => Create(store, b, p, f));
        Assert.True(failure == null, "Native DPAPI is available but the new scoped provisioning behavior is absent/refused");
        Assert.True(File.Exists(p.Credentials)); Assert.False(Directory.Exists(p.Authority)); Assert.False(Directory.Exists(p.Publication));
        using (var lease = store.Open(b, p)) {
            Assert.True(f.Key.AsSpan().SequenceEqual(lease.ApiKey)); Assert.True(f.Secret.AsSpan().SequenceEqual(lease.Secret)); Assert.True(f.Passphrase.AsSpan().SequenceEqual(lease.Passphrase));
            Assert.Equal(b, lease.Binding);
        }
        using (var file = WindowsPathSecurity.Open(p.Credentials)) WindowsPathSecurity.Validate(file, true);
        using (var dir = WindowsPathSecurity.Open(p.Root)) WindowsPathSecurity.Validate(dir, true);
        byte[] cipher = File.ReadAllBytes(p.Credentials);
        Assert.False(cipher.AsSpan().IndexOf(f.Secret) >= 0); Assert.False(cipher.AsSpan().IndexOf(f.Key) >= 0);
        var before = Inventory(h.Root);
        Assert.ThrowsAny<Exception>(() => Create(store, b, p, f)); Assert.Equal(before, Inventory(h.Root));
        using var reopened = store.Open(b, p); Assert.True(f.Key.AsSpan().SequenceEqual(reopened.ApiKey));
    }
    [Fact]
    public async Task CopiedWrongRootEnvironmentBindingAndPurposeRefuseUnchanged()
    {
        NativePrerequisite(); await using var a = new OkxCompositionFixture(); await using var other = new OkxCompositionFixture();
        using var f = new CredentialFixture(); var b = CredentialFixture.Binding(); var live = CredentialFixture.Binding(NavalEnvironment.Live);
        var p = OkxConfiguration.Paths(a.Root, b); var q = OkxConfiguration.Paths(other.Root, b); var l = OkxConfiguration.Paths(a.Root, live);
        var store = new WindowsOkxCredentialStore(a.Root); var second = new WindowsOkxCredentialStore(other.Root);
        Create(store, b, p, f); Create(second, b, q, f); Create(store, live, l, f);
        byte[] original = File.ReadAllBytes(p.Credentials);
        foreach (var destination in new[] { q.Credentials, l.Credentials }) File.WriteAllBytes(destination, original);
        var before = Inventory(a.Root); var beforeOther = Inventory(other.Root);
        Assert.ThrowsAny<Exception>(() => second.Open(b, q)); Assert.ThrowsAny<Exception>(() => store.Open(live, l));
        Assert.ThrowsAny<Exception>(() => store.Open(live, p)); Assert.ThrowsAny<Exception>(() => store.Open(b, q));
        var changed = b with { CredentialBindingId = "other-binding" }; // inconsistent digest rejected before opening
        Assert.ThrowsAny<Exception>(() => store.Open(changed, p));
        Assert.Equal(before, Inventory(a.Root)); Assert.Equal(beforeOther, Inventory(other.Root));
        var crypt = typeof(WindowsLocalAccess).GetMethod("Crypt", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] wrongPurpose = (byte[])crypt.Invoke(null, new object[] { new byte[] { 1, 2, 3 }, true })!;
        File.WriteAllBytes(p.Credentials, wrongPurpose); before = Inventory(a.Root);
        Assert.ThrowsAny<Exception>(() => store.Open(b, p)); Assert.Equal(before, Inventory(a.Root));
        original[original.Length / 2] ^= 1; File.WriteAllBytes(p.Credentials, original); before = Inventory(a.Root);
        Assert.ThrowsAny<Exception>(() => store.Open(b, p)); Assert.Equal(before, Inventory(a.Root));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task BroadAclOnExistingFileOrDirectoryRefusesWithoutRepair(bool directory)
    {
        NativePrerequisite(); await using var h = new OkxCompositionFixture(); using var f = new CredentialFixture();
        var b = CredentialFixture.Binding(); var p = OkxConfiguration.Paths(h.Root, b); var store = new WindowsOkxCredentialStore(h.Root);
        Create(store, b, p, f);
        var sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (directory) { var dir = new DirectoryInfo(p.Root); var acl = dir.GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow)); dir.SetAccessControl(acl); }
        else { var file = new FileInfo(p.Credentials); var acl = file.GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow)); file.SetAccessControl(acl); }
        var before = Inventory(h.Root);
        Assert.ThrowsAny<Exception>(() => store.Open(b, p)); Assert.ThrowsAny<Exception>(() => Create(store, b, p, f));
        Assert.Equal(before, Inventory(h.Root));
    }
    [Fact]
    public async Task ReparseAliasesAndUnexplainedPartialStateRefuseWithoutRepair()
    {
        NativePrerequisite(); await using var h = new OkxCompositionFixture(); using var f = new CredentialFixture();
        var b = CredentialFixture.Binding(); var p = OkxConfiguration.Paths(h.Root, b); var store = new WindowsOkxCredentialStore(h.Root);
        // Empty preexisting scope is unexplained partial state: creation is create-new scope only.
        string parent = Path.GetDirectoryName(p.Root)!;
        using (var admission = WindowsPrivateData.Open(parent, AppContext.BaseDirectory)) { }
        WindowsPathSecurity.CreatePrivateDirectory(p.Root);
        var before = Inventory(h.Root);
        Assert.ThrowsAny<Exception>(() => Create(store, b, p, f)); Assert.Equal(before, Inventory(h.Root));
        Directory.Delete(p.Root);
        string target = Path.Combine(h.Root, "target"); WindowsPathSecurity.CreatePrivateDirectory(target);
        // Junction uses native cmd mklink /J and does not need symlink privilege.
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + p.Root + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
        Assert.True((File.GetAttributes(p.Root) & FileAttributes.ReparsePoint) != 0);
        byte[] sentinel = { 7, 8, 9 }; File.WriteAllBytes(Path.Combine(target, "sentinel"), sentinel);
        Assert.ThrowsAny<Exception>(() => Create(store, b, p, f)); Assert.ThrowsAny<Exception>(() => store.Open(b, p));
        Assert.True(sentinel.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(target, "sentinel")))); Assert.False(File.Exists(Path.Combine(target, "credentials.dpapi")));
        Directory.Delete(p.Root); // delete junction itself before recursive fixture cleanup
        WindowsPathSecurity.CreatePrivateDirectory(p.Root); File.WriteAllText(Path.Combine(p.Root, "unknown"), "synthetic unexplained bytes"); before = Inventory(h.Root);
        Assert.ThrowsAny<Exception>(() => store.Open(b, p)); Assert.ThrowsAny<Exception>(() => Create(store, b, p, f)); Assert.Equal(before, Inventory(h.Root));
    }
}
