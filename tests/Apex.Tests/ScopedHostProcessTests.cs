using System;
using System.Collections.Generic;
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
using Apex.Core.Model;
using Xunit;

namespace Apex.Tests;
public sealed class ScopedHostProcessTests
{
    [Fact]
    public async Task MissingScopedStateBesideReservedConfigIsNotRecreatedAsFresh()
    {
        await using var h = new Host(); await h.Start(); await h.Connect("APEX");
        await h.Status(a => a.GetProperty("engineReady").GetBoolean());
        Assert.Contains("Skopiuj", await h.Command("setupScope", new { confirmDedicatedDemo = true }));
        var status = await h.Status(a => a.GetProperty("scope").GetProperty("baselineId").GetGuid() != Guid.Empty);
        string label = status.GetProperty("scope").GetProperty("label").GetString()!;
        await h.Connect(label); await h.Status(a => a.GetProperty("scope").GetProperty("eligibleTechnicalTest").GetBoolean());
        await h.Stop();
        string path = Directory.GetFiles(h.Dir, "*.json", SearchOption.AllDirectories).Single(p => p.Contains("/stan/") && Path.GetFileName(p).Contains(label));
        File.Delete(path); await h.Start(); await h.Connect(label, withHistory: false);
        status = await h.Status(a => a.GetProperty("buildError").GetString()!.Length > 0);
        Assert.False(status.GetProperty("engineReady").GetBoolean());
        Assert.False(File.Exists(path)); Assert.Equal(0, h.Opens);
    }

    [Fact]
    public async Task ProductionEntryBootstrapsCoverageUnknownScopeThenRunsOnlyExplicitTechnicalRequest()
    {
        await using var h = new Host(); await h.Start();
        await h.Connect("APEX");
        var status = await h.Status(a => a.GetProperty("engineReady").GetBoolean());
        Assert.Equal(0, h.Opens);
        Assert.Contains("potwier", await h.Command("setupScope"));
        var setup = await h.Command("setupScope", new { confirmDedicatedDemo = true });
        Assert.Contains("Skopiuj", setup);
        status = await h.Status(a => a.TryGetProperty("scope", out var s) && s.GetProperty("baselineId").GetGuid() != Guid.Empty);
        var scope = status.GetProperty("scope"); var label = scope.GetProperty("label").GetString()!;
        Assert.NotEqual("APEX", label); Assert.True(label.Length <= 20);
        Assert.Equal(0, h.Opens);
        await h.Connect(label);
        status = await h.Status(a => a.GetProperty("scope").GetProperty("eligibleTechnicalTest").GetBoolean());
        Assert.True(status.GetProperty("scope").GetProperty("brokerCoverageUnknown").GetBoolean());
        Assert.False(h.LastCoverageRequired);
        Assert.Contains("PASS", await h.Command("start"));
        Assert.Contains("potwier", await h.Command("executionTest", new { symbol = "US500" }));
        Assert.Equal(0, h.Opens);
        Assert.Contains("rozpoczęty", await h.Command("executionTest", new { symbol = "US500", confirmDemoTrade = true }));
        Assert.Equal(1, h.Opens);
        await h.Status(a => a.GetProperty("executionTest").EnumerateArray().Any(x => x.GetString()!.Contains("Krok 1 OK")));
        await h.Status(a => a.GetProperty("executionTest").EnumerateArray().Any(x => x.GetString()!.Contains("Krok 2 OK")), 45);
        status = await h.Status(a => a.GetProperty("engine").GetProperty("tradesTotal").GetInt32() == 1, 45);
        Assert.Equal(7.5, status.GetProperty("engine").GetProperty("realizedTotal").GetDouble());
        Assert.Equal(1, h.Modifies); Assert.True(h.Closes >= 1); Assert.Equal(1, h.Opens);
        Assert.Contains("PASS", await h.Command("start"));
        var files = Directory.GetFiles(h.Dir, "*.json", SearchOption.AllDirectories);
        using var persisted = JsonDocument.Parse(File.ReadAllText(files.Single(p => File.ReadAllText(p).Contains("\"OperationId\""))));
        var admission = persisted.RootElement.GetProperty("OpenAdmissions")[0];
        Assert.Equal(h.OpenId, admission.GetProperty("OperationId").GetString());
        Assert.Equal(0.1, admission.GetProperty("Fill").GetProperty("InitialVolumeUnits").GetDouble());
        Assert.Equal(1, admission.GetProperty("Fill").GetProperty("OpeningDeals").GetArrayLength());
        await h.Stop(); await h.Start();
        await h.Connect(label, withHistory: false);
        status = await h.Status(a => a.GetProperty("engineReady").GetBoolean());
        Assert.False(status.GetProperty("scope").GetProperty("eligibleTechnicalTest").GetBoolean());
        Assert.Equal(1, h.Opens);
        h.HistoryEnabled = true; await h.Publish();
        await h.Status(a => a.GetProperty("scope").GetProperty("eligibleTechnicalTest").GetBoolean(), 20);
        Assert.Contains("PASS", await h.Command("start")); Assert.Equal(1, h.Opens);
    }

    internal sealed class Host : IAsyncDisposable
    {
        internal readonly string Dir = Path.Combine(Path.GetTempPath(), "apex-scoped-host-" + Guid.NewGuid().ToString("N"));
        readonly int port; readonly HttpClient http; Process? process; Task<string>? stdout, stderr;
        ClientWebSocket? ws; CancellationTokenSource? peerLifetime; Task? peer; readonly SemaphoreSlim sendGate = new(1);
        internal bool HistoryEnabled = true; internal int Opens, Modifies, Closes; internal string OpenId = ""; internal bool LastCoverageRequired;
        string key = "", label = "APEX", transport = ""; Guid baseline; string epoch = "synthetic-start:1"; long revision;
        readonly AccountDto account = new() { Number = 78123, Broker = "Pepperstone Scope", Currency = "USD", IsLive = false, Hedging = true, Balance = 10000, Equity = 10000, FreeMargin = 10000 };
        readonly List<PositionDto> positions = new(); readonly List<ClosedDto> closed = new(); readonly List<OpeningDealEvidence> openings = new(); readonly List<ResultMsg> results = new();
        internal Host()
        {
            Directory.CreateDirectory(Dir); using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
            File.WriteAllText(Path.Combine(Dir, "apex.config.json"), JsonSerializer.Serialize(new { HttpPort = port, BridgeTcpPort = 0, PairingKey = "SCOPE-LOCAL", ApiToken = new string('x', 40), OpenBrowserOnStart = false, News = new { Enabled = false } }));
            http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(25) }; http.DefaultRequestHeaders.Add("X-Apex-Token", new string('x', 40));
        }
        internal async Task Start()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "APEX.Trading.Demo.sln"))) root = root.Parent;
            string cfg = AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(root!.FullName, "src/Apex.Desktop/bin", cfg, "net8.0/ApexDesktop.dll")); psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(Dir); psi.ArgumentList.Add("--no-browser");
            process = Process.Start(psi)!; stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
            for (int n = 0; n < 100; n++) { try { await http.GetStringAsync("/api/status"); return; } catch (HttpRequestException) { await Task.Delay(100); } }
            throw new Exception("host did not start");
        }
        internal async Task Connect(string exactLabel, bool withHistory = true)
        {
            await Disconnect(); label = exactLabel; HistoryEnabled = withHistory;
            ws = new ClientWebSocket(); peerLifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/bridge"), peerLifetime.Token);
            peer = PeerLoop();
            await Send(new HelloMsg { Platform = "cTrader", Key = "SCOPE-LOCAL", Account = account, HistoryCapability = HistoryPage.Capability, ScopedCapability = ScopeFrame.Capability, Label = label, BrokerEpoch = epoch, BrokerConnected = true,
                DeploymentInventory = new() { new PlatformStateInventory(label, true, results.Count, false) } });
        }
        ScopeFrame Frame(string snapshot = "") => new() { BaselineId = baseline, Label = label, TransportGeneration = transport, BrokerEpoch = epoch, BrokerConnected = true, PositionRevision = revision, PendingOrderRevision = 0, SnapshotId = snapshot };
        internal async Task Publish()
        {
            await Send(new AccountMsg { Account = account, BrokerState = new BrokerStateMsg { BrokerEpoch = epoch, BrokerConnected = true } });
            await Send(new PositionsMsg { Items = positions.ToList(), PendingOrders = new(), Scoped = Frame() });
            await Send(new QuotesMsg { Items = new() { new QuoteDto { S = "US500", B = 4000, A = 4000.1, T = BridgeProtocol.ToUnixMs(DateTime.UtcNow), O = true, Ttc = 3600, Pv = 1 } } });
        }
        async Task PeerLoop()
        {
            try
            {
                while (ws!.State == WebSocketState.Open)
                {
                    using var memory = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult frame;
                    do { frame = await ws.ReceiveAsync(buffer, peerLifetime!.Token); if (frame.MessageType == WebSocketMessageType.Close) return; memory.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
                    string json = Encoding.UTF8.GetString(memory.ToArray()); string? type = BridgeProtocol.ReadType(json);
                    if (type == "welcome")
                    {
                        var w = BridgeProtocol.Deserialize<WelcomeMsg>(json)!; Assert.True(w.Accepted); transport = w.TransportGeneration; baseline = w.BaselineId;
                        await Publish();
                        await Send(new SymbolsMsg { Items = new() { new SymbolDto { Name = "US500", Description = "US 500 Index", BaseAsset = "US500", QuoteAsset = "USD", PipSize = 1, TickSize = 0.1, Digits = 1, PipValue = 1, LotSize = 1, VolumeMin = 0.1, VolumeStep = 0.1, VolumeMax = 100, CommissionKind = "None", MinCommissionKnown = true, MinCommissionAccount = 0 } } });
                    }
                    else if (type == "historyRequest" && HistoryEnabled)
                    {
                        var request = BridgeProtocol.Deserialize<HistoryRequest>(json)!; await Publish(); LastCoverageRequired = false;
                        await Send(new HistoryPage { RequestId = request.RequestId, Pages = 1, Total = closed.Count, CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Coverage = "unknown", Account = account, Positions = positions.ToList(), Deals = closed.ToList(), Scoped = Frame(Guid.NewGuid().ToString("N")), PendingOrders = new(), OpeningObservations = openings.ToList(), RecoveredResults = results.Where(r => request.RecoverOperations.Contains(r.Id)).ToList() });
                    }
                    else if (type == "margin") { var c = BridgeProtocol.Deserialize<MarginCmd>(json)!; await Send(new MarginResultMsg { Id = c.Id, Margin = 80 }); }
                    else if (type == "open")
                    {
                        var c = BridgeProtocol.Deserialize<OpenCmd>(json)!; Assert.NotNull(c.Scoped); Assert.Equal(Frame() with { SnapshotId = c.Scoped!.SnapshotId }, c.Scoped); Assert.Equal(label, c.Label); Assert.True(c.SlPips > 0 && c.TpPips > 0); Opens++; OpenId = c.Id;
                        var time = BridgeProtocol.FromUnixMs(BridgeProtocol.ToUnixMs(DateTime.UtcNow));
                        var opening = new OpeningDealEvidence { DealId = long.MaxValue - 10, OrderId = 17, PositionId = 42, VolumeUnits = c.Units, Price = 4000.1, TimeUtc = time, Symbol = c.Symbol, Direction = TradeDirection.Long, Label = label, Comment = c.Comment };
                        openings.Add(opening); positions.Add(new PositionDto { Id = 42, Symbol = c.Symbol, Side = c.Side, Units = c.Units, Entry = 4000.1, EntryTime = BridgeProtocol.ToUnixMs(time), Sl = 4000.1 - c.SlPips, Tp = 4000.1 + c.TpPips, Label = label, Comment = c.Comment }); revision++;
                        var result = new ResultMsg { Id = c.Id, Status = "filled", PositionId = 42, Price = 4000.1, OpeningFill = new OpeningFillEvidence(new OpeningRequestEvidence { ExecutionId = c.Id, SignalId = c.SignalId!, Symbol = c.Symbol, Direction = TradeDirection.Long, VolumeUnits = c.Units, StopLossPips = c.SlPips, TakeProfitPips = c.TpPips, Label = c.Label, Comment = c.Comment, MaxSlippagePips = c.MaxSlipPips }, 42, c.Units, 0.1, 4000.1, time, new[] { opening }) };
                        results.Add(result); await Publish(); await Send(result);
                    }
                    else if (type == "modify") { var c = BridgeProtocol.Deserialize<ModifyCmd>(json)!; positions[0].Sl = c.Sl; revision++; Modifies++; await Publish(); await Send(new ResultMsg { Id = c.Id, PositionId = 42, Status = "filled", Price = c.Sl }); }
                    else if (type == "close")
                    {
                        var c = BridgeProtocol.Deserialize<CloseCmd>(json)!; var p = positions.Single(x => x.Id == c.PositionId); Closes++;
                        closed.Add(new ClosedDto { Id = p.Id, ClosingDealId = 51, Symbol = p.Symbol, Label = p.Label, Comment = p.Comment, Side = p.Side, Units = p.Units, Entry = p.Entry, EntryTime = p.EntryTime, Exit = 4010, ExitTime = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Gross = 10, Commission = -1.5, Swap = -1, Net = 7.5 });
                        positions.Clear(); revision++; await Publish(); await Send(new ResultMsg { Id = c.Id, PositionId = 42, Status = "filled", Price = 4010 }); await Send(new ClosedMsg { Position = closed[0] });
                    }
                }
            }
            catch (Exception) when (peerLifetime?.IsCancellationRequested == true || ws?.State != WebSocketState.Open) { }
        }
        async Task Send<T>(T msg)
        { await sendGate.WaitAsync(); try { if (ws?.State == WebSocketState.Open) await ws.SendAsync(Encoding.UTF8.GetBytes(BridgeProtocol.Serialize(msg)), WebSocketMessageType.Text, true, peerLifetime!.Token); } finally { sendGate.Release(); } }
        internal async Task<string> Command(string action, object? args = null)
        {
            using var response = await http.PostAsync("/api/accounts/" + Uri.EscapeDataString(key) + "/command", new StringContent(JsonSerializer.Serialize(new { action, args }), Encoding.UTF8, "application/json"));
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.GetProperty("message").GetString()!;
        }
        internal async Task<JsonElement> Status(Func<JsonElement, bool> predicate, int seconds = 20)
        {
            JsonElement last = default; var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (peer?.IsFaulted == true) await peer;
                using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/status"));
                foreach (var a in doc.RootElement.GetProperty("data").GetProperty("accounts").EnumerateArray()) { key = a.GetProperty("key").GetString()!; last = a.Clone(); if (predicate(a)) return last; }
                await Task.Delay(100);
            }
            throw new TimeoutException("scope status: " + last);
        }
        async Task Disconnect()
        { peerLifetime?.Cancel(); ws?.Abort(); if (peer != null) { try { await peer; } catch (OperationCanceledException) { } } ws?.Dispose(); peerLifetime?.Dispose(); ws = null; peerLifetime = null; peer = null; }
        internal async Task Stop()
        {
            await Disconnect(); if (process != null) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } string logs = await stdout! + await stderr!; string evidence = Path.Combine(Path.GetTempPath(), "apex-autonomy/task1-scope-runtime"); Directory.CreateDirectory(evidence); File.AppendAllText(Path.Combine(evidence, "production-scoped-host.log"), logs); process.Dispose(); process = null; }
        }
        public async ValueTask DisposeAsync() { await Stop(); http.Dispose(); Directory.Delete(Dir, true); }
    }
}
