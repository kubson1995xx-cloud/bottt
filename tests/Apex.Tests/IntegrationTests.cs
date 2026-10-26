using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.PlatformSimulator;
using Xunit;

namespace Apex.Tests
{
    /// <summary>
    /// Prawdziwy proces APEX Desktop (osobny katalog danych, losowe porty, wiadomości wyłączone).
    /// Testy łączą się z nim symulatorem platformy (WebSocket) oraz surowym klientem TCP (ścieżka MT5).
    /// </summary>
    public sealed class DesktopHost : IDisposable
    {
        public const string PairingKey = "TEST-PAROWANIE-123";
        public readonly string Token = new string('t', 40);
        public readonly string DataDir;
        public readonly int HttpPort;
        public readonly int TcpPort;
        public readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly Process _proc;
        private readonly StringBuilder _output = new();

        public DesktopHost()
        {
            DataDir = Path.Combine(Path.GetTempPath(), "apex-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDir);
            HttpPort = FreePort();
            TcpPort = FreePort();
            File.WriteAllText(Path.Combine(DataDir, "apex.config.json"), JsonSerializer.Serialize(new
            {
                HttpPort, BridgeTcpPort = TcpPort, PairingKey, ApiToken = Token, OpenBrowserOnStart = false,
                News = new { Enabled = false },
            }));

            string dll = typeof(SyntheticHostEntry).Assembly.Location; // explicit trusted synthetic composition, not production CLI
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(dll)!,
            };
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(DataDir);
            psi.ArgumentList.Add("--no-browser");
            _proc = Process.Start(psi)!;
            _proc.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var r = Get("/api/status").GetAwaiter().GetResult();
                    if (r.StatusCode == HttpStatusCode.OK) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (_proc.HasExited) break;
                Thread.Sleep(300);
            }
            throw new InvalidOperationException("APEX Desktop nie wystartował: " + Output);
        }

        public string Output { get { lock (_output) return _output.ToString(); } }
        public string WsUrl => $"ws://127.0.0.1:{HttpPort}/bridge";

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static string LocateDesktopDll()
        {
            string baseDir = AppContext.BaseDirectory;
            string config = baseDir.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar) ? "Release" : "Debug";
            var dir = new DirectoryInfo(baseDir);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Apex.Desktop", "Apex.Desktop.csproj"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
            string dll = Path.Combine(dir.FullName, "src", "Apex.Desktop", "bin", config, "net8.0", "ApexDesktop.dll");
            if (!File.Exists(dll)) throw new InvalidOperationException("Brak " + dll + " – zbuduj projekt Apex.Desktop.");
            return dll;
        }

        public Task<HttpResponseMessage> Get(string path, bool token = true, string? host = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{HttpPort}{path}");
            if (token) req.Headers.Add("X-Apex-Token", Token);
            if (host != null) req.Headers.Host = host;
            return Http.SendAsync(req);
        }

        public async Task<string> Command(string accountKey, string action, object? args = null, string contentType = "application/json")
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{HttpPort}/api/accounts/{Uri.EscapeDataString(accountKey)}/command")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { action, args }), Encoding.UTF8, contentType),
            };
            req.Headers.Add("X-Apex-Token", Token);
            var r = await Http.SendAsync(req);
            string body = await r.Content.ReadAsStringAsync();
            if (r.StatusCode != HttpStatusCode.OK) return $"HTTP {(int)r.StatusCode}: {body}";
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("message").GetString() ?? "";
        }

        /// <summary>Status konta z /api/status (null, gdy konto jeszcze nie istnieje).</summary>
        public async Task<JsonElement?> Account(string key)
        {
            var r = await Get("/api/status");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            foreach (var a in doc.RootElement.GetProperty("data").GetProperty("accounts").EnumerateArray())
                if (a.GetProperty("key").GetString() == key) return a.Clone();
            return null;
        }

        public void Dispose()
        {
            try { if (!_proc.HasExited) { _proc.Kill(true); _proc.WaitForExit(5000); } } catch (InvalidOperationException) { }
            Http.Dispose();
            try { Directory.Delete(DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Symulator platformy działający w tle do końca testu.</summary>
    internal sealed class RunningSim : IAsyncDisposable
    {
        public readonly Apex.PlatformSimulator.PlatformSimulator Sim;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        public readonly string Key;

        public RunningSim(DesktopHost host, long account, bool live = false, string? key = null)
        {
            var o = new SimOptions { Url = host.WsUrl, Key = key ?? DesktopHost.PairingKey, AccountNumber = account, IsLive = live, HistoryHours = 1500, QuoteIntervalMs = 300 };
            Sim = new Apex.PlatformSimulator.PlatformSimulator(o);
            Key = $"{o.Platform}:{new string(o.Broker.Where(char.IsLetterOrDigit).ToArray())}:{account}";
            _run = Task.Run(() => Sim.RunAsync(_cts.Token));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await Sim.DisconnectAsync(); } catch (Exception) { }
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            await Sim.DisposeAsync();
        }
    }

    public class IntegrationTests : IClassFixture<DesktopHost>
    {
        private readonly DesktopHost _host;
        public IntegrationTests(DesktopHost host) => _host = host;

        private static async Task<bool> WaitFor(Func<Task<bool>> cond, int seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                if (await cond()) return true;
                await Task.Delay(250);
            }
            return await cond();
        }

        private static Task<bool> WaitFor(Func<bool> cond, int seconds) => WaitFor(() => Task.FromResult(cond()), seconds);

        private async Task<JsonElement> WaitEngineReady(string key)
        {
            JsonElement? acc = null;
            Assert.True(await WaitFor(async () => (acc = await _host.Account(key)) is { } a && a.GetProperty("engineReady").GetBoolean(), 45),
                "silnik konta nie został przygotowany: " + _host.Output);
            return acc!.Value;
        }

        [Fact]
        public async Task Api_IsProtected_TokenHostAndContentType()
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Get("/api/status", token: false)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _host.Get("/api/status", host: "evil.example:80")).StatusCode);
            string r = await _host.Command("x", "start", contentType: "text/plain");
            Assert.StartsWith("HTTP 415", r);
            var index = await (await _host.Get("/", token: false)).Content.ReadAsStringAsync();
            Assert.Contains(_host.Token, index); // token wstrzykiwany wyłącznie do strony serwowanej lokalnie
        }

        [Fact]
        public async Task Bridge_WithBrowserOrigin_IsRefused()
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "http://evil.example");
            await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(_host.WsUrl), CancellationToken.None));
        }

        [Fact]
        public async Task WrongPairingKey_IsRejected()
        {
            await using var s = new RunningSim(_host, 700001, key: "ZLY-KLUCZ");
            Assert.True(await WaitFor(() => s.Sim.RejectReason != null, 15));
            Assert.False(s.Sim.Accepted);
            Assert.Contains("klucz", s.Sim.RejectReason);
            Assert.Null(await _host.Account(s.Key));
        }

        [Fact]
        public async Task Connect_ExcludesCrypto_AndSubscribesOnlyTradeableClasses()
        {
            await using var s = new RunningSim(_host, 700002);
            Assert.True(await WaitFor(() => s.Sim.Accepted, 15), s.Sim.RejectReason);
            var acc = await WaitEngineReady(s.Key);
            Assert.True(await WaitFor(() => s.Sim.SubscribeCount > 0, 20));
            Assert.DoesNotContain("BTCUSD", s.Sim.Subscribed, StringComparer.OrdinalIgnoreCase);
            var btc = acc.GetProperty("universe").EnumerateArray().Single(u => u.GetProperty("symbol").GetString() == "BTCUSD");
            Assert.False(btc.GetProperty("selected").GetBoolean());
            Assert.Contains("kryptowaluta", btc.GetProperty("reason").GetString());
            Assert.False(acc.GetProperty("liveFeature").GetBoolean());
        }

        [Fact]
        public async Task DuplicateConnectionForSameAccount_IsRejected()
        {
            await using var a = new RunningSim(_host, 700003);
            Assert.True(await WaitFor(() => a.Sim.Accepted, 15));
            await using var b = new RunningSim(_host, 700003);
            Assert.True(await WaitFor(() => b.Sim.RejectReason != null, 15));
            Assert.False(b.Sim.Accepted);
            Assert.Contains("już połączone", b.Sim.RejectReason);
        }

        [Fact]
        public async Task Reconnect_AfterDisconnect_IsAccepted()
        {
            var a = new RunningSim(_host, 700004);
            Assert.True(await WaitFor(() => a.Sim.Accepted, 15));
            await a.DisposeAsync();
            await using var b = new RunningSim(_host, 700004);
            Assert.True(await WaitFor(() => b.Sim.Accepted, 20), b.Sim.RejectReason);
        }

        [Fact]
        public async Task LiveAccount_IsBlocked_NoOrdersPossible()
        {
            await using var s = new RunningSim(_host, 700005, live: true);
            Assert.True(await WaitFor(() => s.Sim.Accepted, 15));
            await WaitEngineReady(s.Key);
            string start = await _host.Command(s.Key, "start");
            Assert.StartsWith("Nie można uruchomić", start);
            string test = await _host.Command(s.Key, "executionTest", new { symbol = "US500" });
            Assert.DoesNotContain("rozpoczęty", test);
            string unlock = await _host.Command(s.Key, "unlockLive", new { phrase = "LIVE 700005 AKCEPTUJE RYZYKO" });
            Assert.Contains("wyłączony globalnie", unlock); // bez świadomej zmiany pliku konfiguracji LIVE nie da się odblokować
            await Task.Delay(3000);
            Assert.Equal(0, s.Sim.OpenCommands);
            Assert.Empty(s.Sim.Positions);
        }

        [Fact]
        public async Task ExecutionTest_EndToEnd_AndForeignPositionsUntouched()
        {
            await using var s = new RunningSim(_host, 700006);
            Assert.True(await WaitFor(() => s.Sim.Accepted, 15));
            await WaitEngineReady(s.Key);
            await s.Sim.AddForeignPosition("US500", "InnyBot");
            await s.Sim.AddForeignPosition("NAS100", "");
            var foreignIds = s.Sim.Positions.Select(p => p.Id).ToList();

            // Używamy tej samej ścieżki domyślnego instrumentu co przycisk panelu, nie ręcznego obejścia.
            string msg = await _host.Command(s.Key, "executionTest");
            Assert.True(msg.Contains("rozpoczęty"), msg + " STATUS " + await _host.Account(s.Key) + " HOST " + _host.Output);
            string? verdict = null;
            Assert.True(await WaitFor(async () =>
            {
                var acc = await _host.Account(s.Key);
                verdict = acc?.GetProperty("executionTest").EnumerateArray().Select(x => x.GetString()).LastOrDefault(l => l != null && l.Contains("TEST "));
                return verdict != null && (verdict.Contains("ZALICZONY") || verdict.Contains("NIEZALICZONY") || verdict.Contains("PRZERWANY"));
            }, 120), "test wykonania nie zakończył się");
            Assert.Contains("TEST ZALICZONY", verdict);

            await _host.Command(s.Key, "emergency");
            await Task.Delay(4000);
            var left = s.Sim.Positions.Select(p => p.Id).ToList();
            Assert.All(foreignIds, id => Assert.Contains(id, left));
            Assert.DoesNotContain(s.Sim.Positions, p => p.Label == "APEX");
        }

        [Fact]
        public async Task TcpBridge_Handshake_ChecksPairingKey()
        {
            async Task<string> Hello(string key, long account)
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, _host.TcpPort);
                var stream = c.GetStream();
                string hello = "{\"type\":\"hello\",\"protocol\":1,\"platform\":\"MT5-test\",\"bridgeVersion\":\"t\",\"key\":\"" + key +
                               "\",\"account\":{\"number\":" + account + ",\"broker\":\"Test\",\"currency\":\"USD\",\"isLive\":false,\"balance\":1000,\"equity\":1000,\"freeMargin\":1000}}\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(hello));
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                return line ?? "";
            }

            string bad = await Hello("zly", 700007);
            Assert.Contains("\"accepted\":false", bad);
            string good = await Hello(DesktopHost.PairingKey, 700008);
            Assert.Contains("\"accepted\":true", good);
        }
    }
}
