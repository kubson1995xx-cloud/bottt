using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Windows.Tests;

public sealed partial class WindowsLocalSetupTests
{
    // Reflection keeps the RED source buildable on the actual pre-Task3 host; missing
    // contracts fail assertions, not compiler errors. Never emulate a missing API.
    private static object? Call(string type, string method, params object?[] args)
    {
        var t = typeof(ConfigStore).Assembly.GetType("Apex.Desktop.Services." + type);
        Assert.True(t != null, "Missing Task3 contract: " + type);
        var m = t!.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length);
        Assert.True(m != null, "Missing Task3 contract: " + type + "." + method);
        try { return m!.Invoke(null, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
    private static object Ensure(string root, Action<string>? afterWrite = null) =>
        Call("WindowsLocalSetup", "Ensure", root, Presentation().Directory, afterWrite)!;
    private static string State(string root) => Call("WindowsLocalSetup", "Classify", root)!.ToString()!;
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(value)!;
    private static string Map(string root) => Path.Combine(root, "private", "local-access.json");
    private static string Blob(string root) => Path.Combine(root, "private", "local-access.dpapi");
    private static (string Operator, string Reader) Secrets(string root)
    {
        // Independent real CurrentUser DPAPI decrypt, with production's fixed purpose.
        byte[] raw = Unprotect(File.ReadAllBytes(Blob(root)), "apex-local-access-v1");
        try { using var d = JsonDocument.Parse(raw); return (d.RootElement.GetProperty("operatorSecret").GetString()!, d.RootElement.GetProperty("readerSecret").GetString()!); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    private sealed class SetupFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexSetupTests-" + Guid.NewGuid().ToString("N"));
        public WindowsPrivateDataLease Lease { get; }
        public SetupFixture() { Assert.True(OperatingSystem.IsWindows(), "Native Windows required"); Lease = WindowsPrivateData.Open(Root, AppContext.BaseDirectory); }
        public Dictionary<string, string> Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(Root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        public void Dispose() { Lease.Dispose(); Directory.Delete(Root, true); }
    }
    private static void Same(Dictionary<string, string> before, SetupFixture f) => Assert.True(before.OrderBy(p => p.Key).SequenceEqual(f.Snapshot().OrderBy(p => p.Key)), "Existing private bytes changed");
    private static void RefusesUnchanged(SetupFixture f)
    {
        var before = f.Snapshot(); var ex = Record.Exception(() => Ensure(f.Root));
        Assert.True(ex is WindowsDataException, "Expected closed local setup error (details withheld)");
        Assert.Equal("HOST_LOCAL_SETUP_INCOMPLETE", ((WindowsDataException)ex!).Code); Same(before, f);
    }

    // Catches legacy ConfigStore construction before pending/access publication.
    [Fact, Trait("Gate", "Source")]
    public void EmptyRootGetsProtectedPresentationProfile()
    {
        using var f = new SetupFixture();
        Assert.Equal("Empty", State(f.Root)); File.WriteAllText(Path.Combine(f.Root, "apex.lock"), ""); Assert.Equal("Empty", State(f.Root));
        var writes = new List<string>(); var result = Ensure(f.Root, writes.Add);
        Assert.Equal(new[] { "pending", "access", "dpapi", "publication", "config", "ready" }, writes);
        Assert.True(Property<bool>(result, "Initialized")); Assert.True(Property<bool>(result, "NeedsCredentialPrompt"));
        Assert.Equal("Ready", State(f.Root)); var cfg = Property<ConfigStore>(result, "Store").Config;
        Assert.True(cfg.NavalRead!.Enabled); Assert.Equal("presentation", cfg.NavalRead.Scope!.Id); Assert.Empty(cfg.Accounts);
        Assert.False(cfg.News.Enabled); Assert.False(cfg.LiveTradingFeature);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(cfg, ConfigStore.Json));
        var risk = json.RootElement.GetProperty("Risk");
        Assert.Equal(.5, risk.GetProperty("RiskPerTradePct").GetDouble()); Assert.Equal(1.5, risk.GetProperty("MaxDailyLossPct").GetDouble());
        Assert.Equal(5, risk.GetProperty("MaxDrawdownPct").GetDouble()); Assert.Equal(5, risk.GetProperty("MaxEffectiveLeverage").GetDouble());
        var secrets = Secrets(f.Root);
        Assert.True(secrets.Operator != secrets.Reader, "Independently random credentials required");
        foreach (string secret in new[] { secrets.Operator, secrets.Reader })
        { Assert.True(secret.Length == 43); Assert.True(Convert.FromBase64String(secret.Replace('-', '+').Replace('_', '/') + "=").Length == 32); }
        using var map = JsonDocument.Parse(File.ReadAllBytes(Map(f.Root))); var ps = map.RootElement.GetProperty("principals");
        Assert.Equal("naval-local-access-v1", map.RootElement.GetProperty("schema").GetString()); Assert.Equal(2, ps.GetArrayLength());
        foreach (var (p, id, role, secret) in new[] { (ps[0], "operator", "Operator", secrets.Operator), (ps[1], "reader", "ReadOnly", secrets.Reader) })
        {
            Assert.Equal(id, p.GetProperty("id").GetString()); Assert.Equal(role, p.GetProperty("role").GetString());
            Assert.True(p.GetProperty("secretSha256").GetString() == Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret))).ToLowerInvariant(), "Only matching lowercase digest may be stored");
            Assert.Equal(new[] { "presentation" }, p.GetProperty("allowedScopeIds").EnumerateArray().Select(x => x.GetString()));
        }
        Assert.NotNull(Apex.Desktop.Naval.NavalAccessPolicy.Load(cfg.NavalRead));
        foreach (string p in Directory.GetFileSystemEntries(f.Root, "*", SearchOption.AllDirectories)) WindowsPathSecurity.Validate(Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p), true);
        var before = f.Snapshot(); var restart = Ensure(f.Root); Same(before, f);
        Assert.False(Property<bool>(restart, "Initialized")); Assert.False(Property<bool>(restart, "NeedsCredentialPrompt"));
    }
    [Fact, Trait("Gate", "Source")]
    public void ExistingConfigAndCustomProfileAreBytePreserved()
    {
        using var f = new SetupFixture(); var cfg = new ConfigStore(f.Root);
        cfg.Update(c => { c.HttpPort = 5123; c.ProfilePath = "custom.json"; c.OpenBrowserOnStart = false; });
        File.WriteAllText(Path.Combine(f.Root, "custom.json"), "{\"preserve\":true}");
        var before = f.Snapshot(); Assert.Equal("Existing", State(f.Root)); var result = Ensure(f.Root);
        Assert.False(Property<bool>(result, "Initialized")); Assert.Null(Property<ConfigStore>(result, "Store").Config.NavalRead); Same(before, f);
    }
    [Theory, Trait("Gate", "Source")]
    [InlineData("leftover")][InlineData("pending")][InlineData("missing-map")][InlineData("missing-config")]
    [InlineData("corrupt-config")][InlineData("disabled")][InlineData("corrupt-map")][InlineData("foreign-map")][InlineData("marker")][InlineData("publication")]
    public void PendingSetupAndMissingMapNeverDowngrade(string fault)
    {
        using var f = new SetupFixture();
        if (fault == "leftover") { File.WriteAllText(Path.Combine(f.Root, "unowned.tmp"), "preserve"); Assert.Equal("Existing", State(f.Root)); }
        else
        {
            Ensure(f.Root);
            if (fault == "pending" || fault == "marker") File.WriteAllText(Path.Combine(f.Root, "local-startup.json"), fault == "marker" ? "{" : File.ReadAllText(Path.Combine(f.Root, "local-startup.json")).Replace("ready", "pending"));
            if (fault == "missing-map") File.Delete(Map(f.Root));
            if (fault == "missing-config") File.Delete(Path.Combine(f.Root, "apex.config.json"));
            if (fault == "corrupt-config") File.WriteAllText(Path.Combine(f.Root, "apex.config.json"), "{");
            if (fault == "disabled") { var n = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Root, "apex.config.json")))!; n["NavalRead"]!["Enabled"] = false; File.WriteAllText(Path.Combine(f.Root, "apex.config.json"), n.ToJsonString()); }
            if (fault == "corrupt-map") File.WriteAllText(Map(f.Root), "{");
            if (fault == "foreign-map") File.WriteAllText(Map(f.Root), File.ReadAllText(Map(f.Root)).Replace("presentation", "foreign"));
            if (fault == "publication") File.AppendAllText(Path.Combine(f.Root, "publication", "naval-publication.json"), " ");
        }
        RefusesUnchanged(f);
    }
    [Theory, Trait("Gate", "Source")]
    [InlineData("pending")][InlineData("access")][InlineData("dpapi")][InlineData("publication")][InlineData("config")][InlineData("ready")]
    public void CrashAtEachSetupWriteNeverCreatesLegacyBootstrap(string point)
    {
        using var f = new SetupFixture();
        Assert.Throws<InjectedCrash>(() => Ensure(f.Root, p => { if (p == point) throw new InjectedCrash(); }));
        if (point != "ready") RefusesUnchanged(f);
        else { var before = f.Snapshot(); var result = Ensure(f.Root); Assert.True(Property<ConfigStore>(result, "Store").Config.NavalRead!.Enabled); Same(before, f); }
    }
    private sealed class InjectedCrash : Exception { }
    [Theory, Trait("Gate", "Source")]
    [InlineData("corrupt")][InlineData("foreign-purpose")][InlineData("other-root")]
    public void DpapiCorruptionDoesNotRegenerate(string fault)
    {
        using var f = new SetupFixture(); Ensure(f.Root);
        if (fault == "other-root") { using var other = new SetupFixture(); Ensure(other.Root); File.Copy(Blob(other.Root), Blob(f.Root), true); }
        else if (fault == "foreign-purpose") File.WriteAllBytes(Blob(f.Root), Protect(Encoding.UTF8.GetBytes("{}"), "different-purpose"));
        else { var bytes = File.ReadAllBytes(Blob(f.Root)); bytes[bytes.Length / 2] ^= 255; File.WriteAllBytes(Blob(f.Root), bytes); }
        RefusesUnchanged(f);
        Assert.Throws<WindowsDataException>(() => Call("WindowsLocalAccess", "Show", f.Root, new Func<string, string, bool>((_, _) => throw new Exception("Must not prompt"))));
    }
    [Fact, Trait("Gate", "Source")]
    public void PlaintextNeverReachesOutputOrDocuments()
    {
        using var f = new SetupFixture(); Ensure(f.Root); var secrets = Secrets(f.Root); var before = f.Snapshot();
        Assert.True((bool)Call("WindowsLocalAccess", "Show", f.Root, new Func<string, string, bool>((login, secret) => login == "operator" && secret == secrets.Operator))!); Same(before, f);
        foreach (string path in Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories))
        {
            string text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.True(!text.Contains(secrets.Operator) && !text.Contains(secrets.Reader), "Plaintext leaked to a file (content withheld)");
        }
    }
    [Fact, Trait("Gate", "Source")]
    public async Task SetupCancelledKeepsReadyPrivateStateWithoutListener()
    {
        using var f = new SetupFixture(); int port = FreeSetupPort(); bool shown = false;
        var task = (Task<int>)CallProgram(new[] { "--data", f.Root, "--port", port.ToString(), "--no-browser" }, (_, _) => { shown = true; return false; });
        Assert.Equal(2, await task.WaitAsync(TimeSpan.FromSeconds(30))); Assert.True(shown); Assert.Equal("Ready", State(f.Root)); Assert.False(await SetupListening(port));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "logi")));
        var before = f.Snapshot(); Ensure(f.Root); Same(before, f);
    }
    private static object CallProgram(string[] args, Func<string, string, bool> prompt)
    {
        var method = typeof(Apex.Desktop.Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).SingleOrDefault(m => m.Name == "Run" && m.GetParameters().Length == 3);
        Assert.True(method != null, "Missing private source prompt seam"); return method!.Invoke(null, new object[] { args, false, prompt })!;
    }
    [Fact, Trait("Gate", "Source")]
    public async Task LocalAccessDisplayDoesNotStartHost()
    {
        using var f = new SetupFixture(); Ensure(f.Root); var before = f.Snapshot(); int port = FreeSetupPort(); int shown = 0;
        Assert.Equal(0, await ((Task<int>)CallProgram(new[] { "--data", f.Root, "--port", port.ToString(), "--show-local-access" }, (_, _) => { shown++; return true; })).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, shown); Assert.False(await SetupListening(port)); Assert.False(Directory.Exists(Path.Combine(f.Root, "logi"))); Same(before, f);
        using var empty = new SetupFixture(); Assert.Equal(2, await ((Task<int>)CallProgram(new[] { "--data", empty.Root, "--show-local-access" }, (_, _) => throw new Exception("Must not prompt"))).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(new[] { "apex.lock" }, Directory.GetFiles(empty.Root).Select(Path.GetFileName));
    }
    private static int FreeSetupPort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
    private static async Task<bool> SetupListening(int port) { using var c = new TcpClient(); try { await c.ConnectAsync(IPAddress.Loopback, port); return true; } catch (SocketException) { return false; } }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    private static byte[] Unprotect(byte[] bytes, string purpose) => Crypt(bytes, purpose, false);
    private static byte[] Protect(byte[] bytes, string purpose) => Crypt(bytes, purpose, true);
    private static byte[] Crypt(byte[] bytes, string purpose, bool protect)
    {
        byte[] entropy = Encoding.UTF8.GetBytes(purpose);
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; var salt = new DataBlob { Length = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) }; DataBlob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(entropy, 0, salt.Data, entropy.Length);
            bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output);
            Assert.True(ok, "Independent native CurrentUser DPAPI operation failed"); byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
            Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(salt.Data);
        }
    }
}
