using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Xunit;

namespace Apex.Tests
{
    public sealed class DesktopBackendHostTests
    {
        // Rzeczywisty proces Desktop, ale wyłącznie syntetyczne ramki lokalnego mostu, bez brokera.
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public async Task OptionalTcpFailureLeavesWebSocketAvailableAndLiveCannotBeEnabled(bool invalidPort, bool live)
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex backend spaces " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using var busy = new TcpListener(IPAddress.Loopback, 0);
            busy.Start();
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            const string token = "desktop-test-token-long-enough-1234567890";
            const string pairing = "TEST-PAIRING";
            File.WriteAllText(Path.Combine(dir, "apex.config.json"), JsonSerializer.Serialize(new
            {
                HttpPort = port, BridgeTcpPort = invalidPort ? -1 : ((IPEndPoint)busy.LocalEndpoint).Port,
                ApiToken = token, PairingKey = pairing, LiveTradingFeature = true, OpenBrowserOnStart = false,
                News = new { Enabled = false },
            }));
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "Apex.Desktop", "Apex.Desktop.csproj"))) root = root.Parent;
            Assert.NotNull(root);
            string configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar) ? "Release" : "Debug";
            string dll = Path.Combine(root!.FullName, "src", "Apex.Desktop", "bin", configuration, "net8.0", "ApexDesktop.dll");
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(dll); psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(dir); psi.ArgumentList.Add("--no-browser");
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
                http.DefaultRequestHeaders.Add("X-Apex-Token", token);
                JsonElement status = default;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    try { status = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/status")); break; }
                    catch (HttpRequestException) { }
                    if (proc.HasExited) throw new InvalidOperationException(await stdout + await stderr);
                    await Task.Delay(50);
                }
                Assert.Equal(JsonValueKind.Object, status.ValueKind);
                Assert.False(status.GetProperty("host").GetProperty("liveFeature").GetBoolean());
                using var ws = new ClientWebSocket();
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/bridge"), ct.Token);
                var account = new AccountDto { Number = 920001, Broker = "Pepperstone", Currency = "USD", IsLive = live, Balance = 11000, Equity = 10900, Margin = 100, FreeMargin = 10800 };
                async Task Send<T>(T msg) => await ws.SendAsync(Encoding.UTF8.GetBytes(BridgeProtocol.Serialize(msg)), WebSocketMessageType.Text, true, ct.Token);
                await Send(new HelloMsg { Key = pairing, Platform = "cTrader", Account = account });
                var buffer = new byte[8192];
                var reply = await ws.ReceiveAsync(buffer, ct.Token);
                using var welcome = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, reply.Count));
                Assert.True(welcome.RootElement.GetProperty("accepted").GetBoolean());
                await Send(new SymbolsMsg()); await Send(new AccountMsg { Account = account }); await Send(new PositionsMsg());
                JsonElement found = default;
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    status = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/status"));
                    var accounts = status.GetProperty("data").GetProperty("accounts");
                    if (accounts.GetArrayLength() > 0 && accounts[0].GetProperty("engineReady").GetBoolean()) { found = accounts[0].Clone(); break; }
                    await Task.Delay(50);
                }
                Assert.Equal(JsonValueKind.Object, found.ValueKind);
                Assert.Equal("Stopped", found.GetProperty("engine").GetProperty("mode").GetString());
                Assert.Equal(!live, found.GetProperty("engine").GetProperty("guardAllowed").GetBoolean());
                Assert.Equal(10800, found.GetProperty("account").GetProperty("freeMargin").GetDouble());
                string key = found.GetProperty("key").GetString()!;
                using var content = new StringContent("{\"action\":\"unlockLive\",\"args\":{\"phrase\":\"LIVE 920001 AKCEPTUJE RYZYKO\"}}", Encoding.UTF8, "application/json");
                string result = await (await http.PostAsync("/api/accounts/" + Uri.EscapeDataString(key) + "/command", content)).Content.ReadAsStringAsync();
                using var command = JsonDocument.Parse(result);
                Assert.Contains("wyłączony globalnie", command.RootElement.GetProperty("message").GetString());
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/ai/state")).StatusCode);
                ws.Abort();
            }
            finally
            {
                if (!proc.HasExited) { proc.Kill(true); await proc.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr);
                Directory.Delete(dir, true);
            }
        }
    }
}
