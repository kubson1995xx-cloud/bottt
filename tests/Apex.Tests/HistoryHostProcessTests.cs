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
using Apex.Core.Bridge;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;
public sealed partial class HistoryIntegrationTests
{
    [Fact]
    public async Task RealHostProcessReplaysPartialDealsAndRetriesFullCloseAcrossRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "apex-history-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        const string token = "history-local-synthetic-token-1234567890";
        const string pairing = "SYNTHETIC-HISTORY";
        const string key = "cTrader:PepperstoneTest:123456";
        string accountDir = Path.Combine(dir, "konta", "cTrader_PepperstoneTest_123456");
        File.WriteAllText(Path.Combine(dir, "apex.config.json"), JsonSerializer.Serialize(new
        { HttpPort = port, BridgeTcpPort = 0, ApiToken = token, PairingKey = pairing, OpenBrowserOnStart = false, News = new { Enabled = false } }));
        var seedBroker = new RecoveryTests.HistoryBroker { Now = RecoveryTests.Now };
        seedBroker.Positions.Add(RecoveryTests.Open());
        var seed = RecoveryTests.Engine(seedBroker, new FileStore(Path.Combine(accountDir, "stan")), new FileJournal(Path.Combine(accountDir, "dziennik")));
        // A synthetic previously persisted ENTRY plan, not a reconstructed initial-risk guess.
        seed.Plans[42].Reconstructed = false; seed.Plans[42].RiskAmount = 20; seed.SaveState(RecoveryTests.Now, true);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "APEX.Trading.Demo.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar) ? "Release" : "Debug";
        string dll = Path.Combine(root!.FullName, "src", "Apex.Desktop", "bin", configuration, "net8.0", "ApexDesktop.dll");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.Add("X-Apex-Token", token);
        var foreign = new PositionDto { Id = 43, Label = "APEX-other", Symbol = "US500", Units = 1, Entry = 4000, Sl = 3990, Tp = 4020 };
        var own = new PositionDto { Id = 42, Label = "APEX", Symbol = "US500", Units = 1, Entry = 4000, Sl = 3990, Tp = 4020, Net = 9, Commission = -1.5, Swap = -1,
            EntryTime = BridgeProtocol.ToUnixMs(RecoveryTests.Now.AddHours(-2)) };
        var deal1 = Deal(deal: 1); var deal2 = Deal(deal: 2);
        string logs = "";
        try
        {
            for (int run = 0; run < 2; run++)
            {
                var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add(dll); psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(dir); psi.ArgumentList.Add("--no-browser");
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
                using var ws = new ClientWebSocket(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                try
                {
                    await WaitStatus(_ => true, 20, allowNoAccount: true);
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/bridge"), ct.Token);
                    await Send(new HelloMsg { Platform = "cTrader", Key = pairing, Account = Account, HistoryCapability = HistoryPage.Capability });
                    var welcome = await Receive("welcome"); Assert.True(welcome.GetProperty("accepted").GetBoolean());
                    var request = await Receive("historyRequest");
                    await Send(new SymbolsMsg()); await Send(new PositionsMsg { Items = new() { own, foreign } });
                    await Snapshot(request.GetProperty("requestId").GetString()!, final: false);
                    // Initial request predates the first live positions frame: its capture is fenced.
                    // Respond to the actual retry, rather than relying on frozen history to publish live state.
                    request = await Receive("historyRequest");
                    await Snapshot(request.GetProperty("requestId").GetString()!, final: false);
                    var status = await WaitStatus(a => a.GetProperty("engine").GetProperty("realizedTotal").GetDouble() == 7.5, 15);
                    Assert.Equal(0, status.GetProperty("engine").GetProperty("tradesTotal").GetInt32());
                    Assert.False(status.GetProperty("engine").GetProperty("historyReady").GetBoolean());
                    Assert.Equal("Stopped", status.GetProperty("engine").GetProperty("mode").GetString());
                    var position = status.GetProperty("positions").EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == 42);
                    Assert.Equal(3990, position.GetProperty("sl").GetDouble()); Assert.Equal(4020, position.GetProperty("tp").GetDouble()); Assert.Equal(9, position.GetProperty("net").GetDouble());
                    if (run == 0)
                    {
                        var command = Command("closeAll");
                        var close = await Receive("close"); Assert.Equal(42, close.GetProperty("positionId").GetInt64()); Assert.False(close.TryGetProperty("units", out _));
                        await Send(new ResultMsg { Id = close.GetProperty("id").GetString()!, Status = "uncertain", Error = "Timeout" });
                        await command;
                    }
                    else
                    {
                        Assert.Contains("histori", await Command("start"));
                        var retry = await Receive("close"); Assert.Equal(42, retry.GetProperty("positionId").GetInt64());
                        await Send(new ResultMsg { Id = retry.GetProperty("id").GetString()!, PositionId = 42, Status = "filled", Price = 4010 });
                        await Send(new PositionsMsg { Items = new() { foreign } }); await Send(new ClosedMsg { Position = deal2 });
                        var replay = await Receive("historyRequest"); await Snapshot(replay.GetProperty("requestId").GetString()!, final: true);
                        status = await WaitStatus(a => a.GetProperty("engine").GetProperty("tradesTotal").GetInt32() == 1, 15);
                        Assert.Equal(15, status.GetProperty("engine").GetProperty("realizedTotal").GetDouble());
                        Assert.Equal("Stopped", status.GetProperty("engine").GetProperty("mode").GetString());
                        Assert.Equal(43, Assert.Single(status.GetProperty("positions").EnumerateArray()).GetProperty("id").GetInt64());
                        Assert.Equal(2, File.ReadAllLines(Path.Combine(accountDir, "dziennik", "transakcje.csv")).Length);
                        // Duplicate hint requests replay, not a second PnL credit or CSV row.
                        await Send(new ClosedMsg { Position = deal2 }); replay = await Receive("historyRequest"); await Snapshot(replay.GetProperty("requestId").GetString()!, final: true);
                        status = await WaitStatus(a => a.GetProperty("engine").GetProperty("tradesTotal").GetInt32() == 1, 10);
                        Assert.Equal(15, status.GetProperty("engine").GetProperty("realizedTotal").GetDouble());
                        Assert.Equal(2, File.ReadAllLines(Path.Combine(accountDir, "dziennik", "transakcje.csv")).Length);
                    }
                }
                finally
                {
                    ws.Abort(); if (!proc.HasExited) { proc.Kill(true); await proc.WaitForExitAsync(); }
                    logs += $"RUN {run}\n" + await stdout + await stderr;
                }
                async Task Send<T>(T msg) => await ws.SendAsync(Encoding.UTF8.GetBytes(BridgeProtocol.Serialize(msg)), WebSocketMessageType.Text, true, ct.Token);
                async Task<JsonElement> Receive(string type)
                {
                    while (true)
                    {
                        var bytes = new byte[8192]; var text = new StringBuilder(); WebSocketReceiveResult reply;
                        do { reply = await ws.ReceiveAsync(bytes, ct.Token); text.Append(Encoding.UTF8.GetString(bytes, 0, reply.Count)); } while (!reply.EndOfMessage);
                        Assert.NotEqual(WebSocketMessageType.Close, reply.MessageType);
                        using var doc = JsonDocument.Parse(text.ToString());
                        Assert.NotEqual("open", doc.RootElement.GetProperty("type").GetString());
                        if (doc.RootElement.GetProperty("type").GetString() == type) return doc.RootElement.Clone();
                    }
                }
                async Task Snapshot(string requestId, bool final)
                {
                    long capture = BridgeProtocol.ToUnixMs(DateTime.UtcNow);
                    if (final)
                    {
                        await Send(new HistoryPage { RequestId = requestId, Page = 0, Pages = 2, Total = 2, CapturedAt = capture, Deals = new() { deal1 } });
                        await Task.Delay(150); // no terminal row may publish on the first page
                        await Send(new HistoryPage { RequestId = requestId, Page = 1, Pages = 2, Total = 2, CapturedAt = capture, Deals = new() { deal2 }, Account = Account, Positions = new() { foreign } });
                    }
                    else await Send(new HistoryPage { RequestId = requestId, Page = 0, Pages = 1, Total = 1, CapturedAt = capture, Coverage = "required", Deals = new() { deal1 }, Account = Account, Positions = new() { own, foreign } });
                }
            }
        }
        finally
        {
            string evidence = Path.Combine(Path.GetTempPath(), "apex-autonomy", "task1"); Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "host-process-history.log"), logs);
            Directory.Delete(dir, true);
        }
        async Task<string> Command(string action)
        {
            using var response = await http.PostAsync("/api/accounts/" + Uri.EscapeDataString(key) + "/command", new StringContent(JsonSerializer.Serialize(new { action }), Encoding.UTF8, "application/json"));
            using var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return parsed.RootElement.GetProperty("message").GetString()!;
        }
        async Task<JsonElement> WaitStatus(Func<JsonElement, bool> predicate, int seconds, bool allowNoAccount = false)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/status"));
                    var accounts = doc.RootElement.GetProperty("data").GetProperty("accounts");
                    if (allowNoAccount) return doc.RootElement.Clone();
                    foreach (var a in accounts.EnumerateArray())
                        if (a.GetProperty("key").GetString() == key && a.GetProperty("engineReady").GetBoolean() && predicate(a)) return a.Clone();
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidOperationException) { }
                await Task.Delay(75);
            }
            throw new TimeoutException("host status predicate: " + logs);
        }
    }
}
