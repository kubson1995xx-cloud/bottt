using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Apex.Desktop;
using Apex.Desktop.Bridge;
using Apex.Desktop.Runtime;
using Apex.Desktop.Services;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Apex.Windows.Tests;

[CollectionDefinition("Windows startup", DisableParallelization = true)]
public sealed class WindowsStartupCollection { }

[Collection("Windows startup")]
public sealed class WindowsStartupTests
{
    // Each case runs real admission, config, Kestrel and cleanup. Only the external
    // shell operation is intercepted; this is not actual browser/UI acceptance.
    private sealed class Host : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexStartup-" + Guid.NewGuid().ToString("N"));
        internal readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(40));
        internal readonly int Port = FreePort();
        internal readonly Dictionary<string, object?> Resources = new();
        internal readonly List<ProcessStartInfo> Launches = new();
        internal readonly ConfigStore Store;
        internal Task<int>? Running;
        private readonly TextWriter previousOutput = Console.Out;
        private readonly StringWriter privateOutput = new();
        internal Host(bool open = true, int tcp = 0)
        {
            Assert.True(OperatingSystem.IsWindows(), "Native Windows required");
            Console.SetOut(privateOutput);
            using var lease = WindowsPrivateData.Open(Root, AppContext.BaseDirectory);
            Store = new ConfigStore(Root);
            Store.Update(c => { c.HttpPort = Port; c.BridgeTcpPort = tcp; c.OpenBrowserOnStart = open; c.News.Enabled = false; });
        }
        internal Task<int> Start(string[]? extra = null, Action<ProcessStartInfo>? launch = null, Action<string, object?>? observe = null)
        {
            var args = new[] { "--data", Root }.Concat(extra ?? Array.Empty<string>()).ToArray();
            return Running = Task.Run(() => Program.Run(args, false, null, p => { Launches.Add(p); launch?.Invoke(p); }, Stop.Token,
                (stage, value) => { Resources[stage] = value; observe?.Invoke(stage, value); }));
        }
        internal async Task<JsonElement> Ready()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.Add("X-Apex-Token", Store.Config.ApiToken);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                if (Running!.IsCompleted) { await Running; throw new Exception("Host exited before readiness"); }
                try { using var d = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{Port}/api/status")); return d.RootElement.Clone(); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { await Task.Delay(50); }
            }
            throw new TimeoutException("No real HTTP response before deadline");
        }
        internal async Task<int> Finish() { Stop.Cancel(); return await Running!.WaitAsync(TimeSpan.FromSeconds(15)); }
        internal void Released()
        {
            using var file = new FileStream(Path.Combine(Root, "apex.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), p => p.Port == Port);
            if (Resources.TryGetValue("bridge", out var bridge))
            {
                var listener = (TcpListener?)typeof(BridgeServer).GetField("_tcp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge);
                Assert.False(listener?.Server.IsBound == true, "Optional TCP listener survived shutdown");
            }
            if (Resources.TryGetValue("services", out var service))
                Assert.True(((Task?)typeof(NewsService).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service))?.IsCompleted == true, "News worker survived host shutdown");
            if (Resources.TryGetValue("accounts", out var a))
                Assert.True((bool)typeof(AccountManager).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!, "Account manager was not disposed");
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            if (Running != null) { try { await Running.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* original assertion retains the causal failure */ } }
            // RED-only recovery of the old host's leaked resources, after assertions.
            if (Resources.TryGetValue("bridge", out var bridge)) ((BridgeServer)bridge!).Stop();
            if (Resources.TryGetValue("accounts", out var accounts)) ((AccountManager)accounts!).Dispose();
            if (Resources.TryGetValue("app", out var app)) await ((WebApplication)app!).DisposeAsync();
            Stop.Dispose();
            Console.SetOut(previousOutput); privateOutput.Dispose();
            Directory.Delete(Root, true);
        }
    }
    private static int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
    private static object? Browser(string method, params object[] args)
    {
        var type = typeof(Program).Assembly.GetType("Apex.Desktop.Services.DesktopBrowser");
        Assert.NotNull(type);
        var m = type!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Single(x => x.Name == method && x.GetParameters().Length == args.Length);
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    // Catches the old msedge --app preference, URL credential/host expansion and shell args.
    [Fact, Trait("Gate", "Source")]
    public async Task DefaultShellNotEdgeAndUrlHasNoSecrets()
    {
        await using var h = new Host();
        try { await h.Start(launch: _ => h.Stop.Cancel()); } catch (OperationCanceledException) { }
        Assert.True(h.Launches.Count == 1, "Exactly one shell request required");
        var request = h.Launches[0];
        Assert.Equal($"http://127.0.0.1:{h.Port}/", request.FileName);
        Assert.True(request.UseShellExecute); Assert.Equal("", request.Arguments); Assert.Empty(request.ArgumentList);
        var uri = new Uri(request.FileName);
        Assert.Equal("", uri.UserInfo); Assert.Equal("", uri.Query); Assert.Equal("", uri.Fragment);
        Assert.DoesNotContain(h.Store.Config.ApiToken, request.FileName); Assert.DoesNotContain(h.Store.Config.PairingKey, request.FileName);
        h.Released();
    }

    // The shell callback itself must get a real authenticated HTTP response immediately.
    [Fact, Trait("Gate", "Source")]
    public async Task BrowserRequestedOnlyAfterHttpStart()
    {
        await using var h = new Host(); bool reachable = false;
        try { await h.Start(launch: _ =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Add("X-Apex-Token", h.Store.Config.ApiToken);
            try { using var r = http.GetAsync($"http://127.0.0.1:{h.Port}/api/status").GetAwaiter().GetResult(); reachable = r.StatusCode == HttpStatusCode.OK; }
            catch (HttpRequestException) { }
            finally { h.Stop.Cancel(); }
        }); } catch (OperationCanceledException) { }
        Assert.True(reachable, "Browser was requested before the HTTP listener was ready"); Assert.True(h.Launches.Count == 1, "Exactly one shell request required"); h.Released();
    }

    [Theory, Trait("Gate", "Source")]
    [InlineData(false, false)][InlineData(true, true)]
    public async Task SuppressionPreservesNoBrowser(bool configOpen, bool cliSuppress)
    {
        await using var h = new Host(configOpen); _ = h.Start(cliSuppress ? new[] { "--no-browser" } : null);
        var status = await h.Ready(); Assert.Empty(status.GetProperty("data").GetProperty("accounts").EnumerateArray());
        Assert.Equal(0, await h.Finish()); Assert.Empty(h.Launches); h.Released();
    }

    [Fact, Trait("Gate", "Source")]
    public async Task SecondLaunchAndOccupiedPortDoNotNavigate()
    {
        await using var first = new Host(false); _ = first.Start(); await first.Ready();
        var before = File.ReadAllBytes(first.Store.Path_); var calls = new List<ProcessStartInfo>();
        Assert.Throws<IOException>(() => { using var duplicate = new FileStream(Path.Combine(first.Root, "apex.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
        Assert.NotNull(Record.Exception(() => File.Delete(Path.Combine(first.Root, "apex.lock"))));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var previousError = Console.Error; using var error = new StringWriter(); Console.SetError(error);
        try { Assert.Equal(2, await Program.Run(new[] { "--data", first.Root }, false, null, calls.Add, timeout.Token, null)); }
        finally { Console.SetError(previousError); }
        Assert.Contains("HOST_ALREADY_RUNNING", error.ToString());
        Assert.Empty(calls); Assert.Equal(before, File.ReadAllBytes(first.Store.Path_));
        await using var other = new Host();
        Assert.Equal(2, await other.Start(new[] { "--port", first.Port.ToString() }));
        Assert.Empty(other.Launches); other.Released(); await first.Ready();
        Assert.Equal(0, await first.Finish()); first.Released();
    }

    [Fact, Trait("Gate", "Source")]
    public async Task BindFailureReleasesLockAndAccounts()
    {
        await using var h = new Host(); using var foreign = new TcpListener(IPAddress.Loopback, h.Port); foreign.Start();
        var before = File.ReadAllBytes(h.Store.Path_);
        var old = Console.Error; using var error = new StringWriter(); Console.SetError(error);
        try { Assert.Equal(2, await h.Start()); } finally { Console.SetError(old); }
        Assert.Contains("HOST_HTTP_BIND_FAILED", error.ToString());
        Assert.Empty(h.Launches); Assert.Equal(before, File.ReadAllBytes(h.Store.Path_));
        foreign.Stop(); h.Released();
        // Same root/port is usable after refusal, not an alternative port.
        h.Stop.Cancel();
        using var retry = new CancellationTokenSource();
        Assert.Equal(0, await Program.Run(new[] { "--data", h.Root }, false, null, _ => retry.Cancel(), retry.Token, null));
        h.Released();
    }

    [Theory, Trait("Gate", "Source")]
    [InlineData("0")][InlineData("-1")][InlineData("65536")][InlineData("invalid")][InlineData("")]
    public async Task InvalidPortNoListener(string port)
    {
        await using var h = new Host();
        var before = File.ReadAllBytes(h.Store.Path_);
        Assert.Equal(2, await h.Start(new[] { "--port", port })); Assert.Empty(h.Launches);
        Assert.DoesNotContain("accounts", h.Resources.Keys); Assert.Equal(before, File.ReadAllBytes(h.Store.Path_)); h.Released();
    }

    [Theory, Trait("Gate", "Source")]
    [InlineData(0)][InlineData(65536)]
    public async Task InvalidConfiguredPortNoListener(int port)
    {
        await using var h = new Host(); h.Store.Update(c => c.HttpPort = port);
        Assert.Equal(2, await h.Start()); Assert.Empty(h.Launches); Assert.DoesNotContain("accounts", h.Resources.Keys); h.Released();
    }

    [Fact, Trait("Gate", "Source")]
    public async Task TcpCollisionIsDegradedNotFatal()
    {
        int tcp = FreePort(); using var foreign = new TcpListener(IPAddress.Loopback, tcp); foreign.Start();
        await using var h = new Host(tcp: tcp); _ = h.Start();
        var status = await h.Ready(); Assert.False(status.GetProperty("host").GetProperty("liveFeature").GetBoolean());
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{h.Port}/bridge"), h.Stop.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, ws.State); ws.Abort();
        Assert.Contains(Directory.GetFiles(Path.Combine(h.Root, "logi")), p => File.ReadAllText(p).Contains("TCP"));
        Assert.True(h.Launches.Count == 1, "Exactly one shell request required");
        Assert.All(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(x => x.Port == h.Port), x => Assert.Equal(IPAddress.Loopback, x.Address));
        Assert.Equal(0, await h.Finish()); h.Released();
    }

    [Fact, Trait("Gate", "Source")]
    public async Task RestartNeverAutoStarts()
    {
        await using var h = new Host(false);
        h.Store.Update(c => c.Accounts["cTrader:SyntheticStartup:1"] = new AccountSettings { AutoStart = true });
        string state = Path.Combine(h.Root, "konta", "cTrader_SyntheticStartup_1", "stan");
        Directory.CreateDirectory(state);
        string control = Path.Combine(state, "desktop_control.json");
        File.WriteAllText(control, "{\"EmergencyStopped\":true}");
        var before = File.ReadAllBytes(h.Store.Path_); var latch = File.ReadAllBytes(control);
        for (int n = 0; n < 2; n++)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            h.Running = Task.Run(() => Program.Run(new[] { "--data", h.Root }, false, null, null, stop.Token, null));
            await h.Ready();
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{h.Port}/bridge"), stop.Token);
            async Task Send<T>(T msg) => await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(Apex.Core.Bridge.BridgeProtocol.Serialize(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, stop.Token);
            var account = new Apex.Core.Bridge.AccountDto { Number = 1, Broker = "SyntheticStartup", Currency = "USD", Balance = 10000, Equity = 10000, FreeMargin = 10000 };
            await Send(new Apex.Core.Bridge.HelloMsg { Platform = "cTrader", Key = h.Store.Config.PairingKey, Account = account });
            var buffer = new byte[65536]; var welcome = await ws.ReceiveAsync(buffer, stop.Token);
            using (var d = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, welcome.Count))) Assert.True(d.RootElement.GetProperty("accepted").GetBoolean());
            await Send(new Apex.Core.Bridge.SymbolsMsg()); await Send(new Apex.Core.Bridge.AccountMsg { Account = account });
            await Send(new Apex.Core.Bridge.PositionsMsg());
            bool observed = false;
            for (int i = 0; i < 100; i++)
            {
                var status = await h.Ready(); var accounts = status.GetProperty("data").GetProperty("accounts");
                if (accounts.GetArrayLength() == 1 && accounts[0].GetProperty("engineReady").GetBoolean())
                { Assert.Equal("EmergencyStopped", accounts[0].GetProperty("engine").GetProperty("mode").GetString()); observed = true; break; }
                await Task.Delay(50);
            }
            Assert.True(observed, "No stopped engine observed from the synthetic local peer");
            ws.Abort(); stop.Cancel(); Assert.Equal(0, await h.Running);
            Assert.Equal(before, File.ReadAllBytes(h.Store.Path_)); Assert.Equal(latch, File.ReadAllBytes(control));
            h.Released();
        }
    }

    // Catches incomplete lifetime registration at any partially constructed/start stage.
    [Theory, Trait("Gate", "Source")]
    [InlineData("accounts")][InlineData("app")][InlineData("http")][InlineData("services")]
    public async Task PartialStartupFailureReleasesResources(string point)
    {
        await using var h = new Host(); bool reached = false;
        Assert.Equal(2, await h.Start(observe: (stage, _) => { if (stage == point) { reached = true; throw new IOException("private exception must not escape"); } }));
        Assert.True(reached); Assert.Empty(h.Launches); h.Released();
    }

    [Fact, Trait("Gate", "Source")]
    public async Task CancellationBeforeBindNeverNavigates()
    {
        await using var h = new Host(); h.Stop.Cancel();
        Assert.Equal(0, await h.Start()); Assert.Empty(h.Launches); h.Released();
    }

    [Fact, Trait("Gate", "Source")]
    public async Task ShellFailureKeepsReadyHostAndOffersSafeManualUrl()
    {
        await using var h = new Host(); var old = Console.Error; using var error = new StringWriter(); Console.SetError(error);
        try
        {
            _ = h.Start(launch: _ => throw new System.ComponentModel.Win32Exception("private diagnostic"));
            await h.Ready(); Assert.True(h.Launches.Count == 1, "Exactly one shell request required");
            Assert.Contains($"http://127.0.0.1:{h.Port}/", error.ToString()); Assert.DoesNotContain("private diagnostic", error.ToString());
            Assert.Equal(0, await h.Finish()); h.Released();
        }
        finally { Console.SetError(old); }
    }

    [Theory, Trait("Gate", "Source")]
    [InlineData(1, false, "http://127.0.0.1:1/")]
    [InlineData(65535, true, "http://127.0.0.1:65535/?view=naval")]
    public void FixedSafeUri(int port, bool naval, string expected) => Assert.Equal(expected, ((Uri)Browser("BuildUri", port, naval)!).AbsoluteUri);

    [Theory, Trait("Gate", "Source")]
    [InlineData(0)][InlineData(-1)][InlineData(65536)]
    public void UriRejectsInvalidPort(int port) => Assert.Throws<ArgumentOutOfRangeException>(() => Browser("BuildUri", port, true));
}
