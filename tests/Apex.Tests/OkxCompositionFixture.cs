using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;

[CollectionDefinition("OKX composition", DisableParallelization = true)]
public sealed class OkxCompositionCollection { }

internal sealed class OkxCompositionFixture : IAsyncDisposable
{
    internal string Root { get; }
    internal string ConfigPath => Path.Combine(Root, "apex.config.json");
    internal readonly Dictionary<string, object?> Resources = new();
    internal bool ReachedHttp;
    internal Task<int>? Running;
    private CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
    internal Action? HttpAction;
    internal OkxCompositionFixture()
    {
        Root = Path.Combine(OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.GetTempPath(), "ApexOkx-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) { using var admission = WindowsPrivateData.Open(Root, AppContext.BaseDirectory); }
        else Directory.CreateDirectory(Root);
    }
    internal static string ValidConfig(NavalEnvironment env) => "{\"Schema\":\"okx-naval-config-v1\",\"Scopes\":[" + Scope(env) + "]}";
    internal static string Scope(NavalEnvironment env) => JsonSerializer.Serialize(new {
        ScopeId = env == NavalEnvironment.Live ? "synthetic-live" : "synthetic-demo", Environment = env.ToString(), Region = "EEA",
        ExpectedUid = "123456", ExpectedMainUid = "123456", QuoteCurrency = "EUR", CredentialBindingId = "synthetic-binding", SafetyVersion = "okx-cash-v1" });
    internal string Raw(string? section, string name = "OkxNaval")
    {
        var config = new HostConfig { HttpPort = FreePort(), BridgeTcpPort = 0, OpenBrowserOnStart = false, PairingKey = "SYNTHETIC-PAIR", ApiToken = new string('a', 64) };
        config.News.Enabled = false;
        string raw = JsonSerializer.Serialize(config, ConfigStore.Json);
        return section == null ? raw : raw[..^1] + ",\"" + name + "\":" + section + "}";
    }
    internal Task<int> StartHostAsync(string rawConfig, bool synthetic = false)
    {
        File.WriteAllText(ConfigPath, rawConfig);
        return Launch(synthetic);
    }
    internal Task<int> RestartHostAsync() => Launch(false);
    private Task<int> Launch(bool synthetic)
    {
        if (stop.IsCancellationRequested) { stop.Dispose(); stop = new(TimeSpan.FromSeconds(30)); }
        Resources.Clear(); ReachedHttp = false;
        Running = Task.Run(() => Apex.Desktop.Program.Run(new[] { "--data", Root, "--no-browser" }, synthetic, null, null, stop.Token,
            (stage, value) => { Resources[stage] = value; if (stage == "http") { ReachedHttp = true; try { HttpAction?.Invoke(); } finally { stop.Cancel(); } } }));
        return Running.WaitAsync(TimeSpan.FromSeconds(35));
    }
    internal SortedDictionary<string, string> Inventory() => new(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != "apex.lock" && !Path.GetRelativePath(Root, p).Split(Path.DirectorySeparatorChar).Contains("logi"))
        .ToDictionary(p => Path.GetRelativePath(Root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))), StringComparer.Ordinal);
    private static int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (Running != null) await Running.WaitAsync(TimeSpan.FromSeconds(15));
        stop.Dispose();
        using (var file = new FileStream(Path.Combine(Root, "apex.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
        Directory.Delete(Root, true);
    }
}
