using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Desktop.Bridge;
using Apex.Desktop.Runtime;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests
{
    // Wyłącznie lokalny transport pamięciowy; żaden test nie łączy się z brokerem.
    public sealed class DesktopSafetyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-desktop-safety-" + Guid.NewGuid().ToString("N"));
        public DesktopSafetyTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private sealed class Transport : IBridgeTransport
        {
            public string Kind => "test";
            public string Remote => "memory";
            public readonly List<string> Sent = new();
            public Func<CancellationToken, Task>? Block;
            public Task SendAsync(string json, CancellationToken ct)
            {
                lock (Sent) Sent.Add(json);
                return Block?.Invoke(ct) ?? Task.CompletedTask;
            }
            public Task CloseAsync(string reason) => Task.CompletedTask;
        }

        private static AccountDto Account(bool live = false) => new()
        {
            Number = 910001, Broker = "Pepperstone", Currency = "USD", IsLive = live,
            Balance = 12345, Equity = 12000, FreeMargin = 11500, Margin = 500,
        };

        private static BridgeSession Session(Transport? transport = null, bool live = false)
        {
            var s = new BridgeSession(transport ?? new Transport());
            s.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = "cTrader", Account = Account(live) }));
            s.State = SessionState.Active;
            s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
            return s;
        }

        private ConfigStore Config() => new(_dir);
        private AccountRuntime Runtime(ConfigStore? config = null) => new("cTrader:Pepperstone:910001", "cTrader", config ?? Config(), new NewsCalendar(), new AppLog(Path.Combine(_dir, "logs")));
        private static void Symbols(BridgeSession s) => s.Handle(BridgeProtocol.Serialize(new SymbolsMsg()));
        private static Task<string> Action(AccountRuntime r, string action) => r.Command(e => r.ExecuteAction(e, action, default));
        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(condition());
        }
        private static JsonElement Status(AccountRuntime r) => JsonSerializer.SerializeToElement(r.Status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        [Theory]
        [InlineData("isLive")]
        [InlineData("broker")]
        [InlineData("currency")]
        [InlineData("hedging")]
        [InlineData("number")]
        public void AccountIdentityCannotDrift(string field)
        {
            var s = Session();
            var a = Account();
            switch (field)
            {
                case "isLive": a.IsLive = true; break;
                case "broker": a.Broker = "Other"; break;
                case "currency": a.Currency = "EUR"; break;
                case "hedging": a.Hedging = false; break;
                case "number": a.Number++; break;
            }
            s.Handle(BridgeProtocol.Serialize(new AccountMsg { Account = a }));
            Assert.Equal(SessionState.Rejected, s.State);
            Assert.False(new SessionAdapter(() => null) { }.Session?.IsAlive ?? false);
            Assert.Equal("USD", s.Account!.Currency);
        }

        [Fact]
        public void MissingAuthoritativeAccountTypeIsRejected()
        {
            var cfg = Config();
            using var m = new AccountManager(cfg, new NewsCalendar(), new AppLog(Path.Combine(_dir, "logs")));
            var s = new BridgeSession(new Transport());
            m.Register(s);
            s.Handle("{\"type\":\"hello\",\"protocol\":1,\"platform\":\"cTrader\",\"key\":\"" + cfg.Config.PairingKey + "\",\"account\":{\"number\":910001,\"broker\":\"Pepperstone\",\"currency\":\"USD\",\"balance\":10000,\"equity\":10000}}");
            Assert.Equal(SessionState.Rejected, s.State);
            Assert.Empty(m.Runtimes);
        }

        [Fact]
        public void ConnectionRequiresPositionsSnapshot()
        {
            var s = new BridgeSession(new Transport());
            s.Handle(BridgeProtocol.Serialize(new HelloMsg { Account = Account() }));
            s.State = SessionState.Active;
            var a = new SessionAdapter(() => null);
            a.Attach(s);
            Assert.False(a.IsConnected);
            s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
            Assert.True(a.IsConnected);
        }

        [Fact]
        public async Task AccountSnapshotTimeIsReceiptNotReadTime()
        {
            var a = new SessionAdapter(() => null);
            a.Attach(Session());
            var first = a.GetAccount().TimeUtc;
            await Task.Delay(30);
            Assert.Equal(first, a.GetAccount().TimeUtc);
        }

        [Fact]
        public async Task LogsCannotKeepOldAccountAndPositionsTradeable()
        {
            var s = Session();
            var a = new SessionAdapter(() => null);
            a.Attach(s);
            await Task.Delay(TimeSpan.FromSeconds(31));
            s.Handle(BridgeProtocol.Serialize(new LogMsg { Message = "still connected" }));
            Assert.True(s.IsAlive);
            Assert.False(a.IsConnected);
        }

        [Fact]
        public void LiveGatewayNeverSendsTradingCommands()
        {
            var t = new Transport();
            var a = new SessionAdapter(() => null);
            a.Attach(Session(t, true));
            a.OrderTimeout = TimeSpan.FromMilliseconds(20);
            a.OpenMarket(new OrderRequest { Symbol = "EURUSD", VolumeUnits = 1000, StopLossPips = 10, TakeProfitPips = 20 });
            a.ModifyStopLoss(1, 1.2);
            a.ClosePosition(1, null);
            Assert.Empty(t.Sent);
        }

        [Fact]
        public async Task TimedOutSendWaitingForTransportLockNeverExecutesLater()
        {
            var t = new Transport();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            t.Block = _ => release.Task;
            var s = Session(t);
            var first = s.SendAsync(new SimpleMsg());
            var result = await Task.Run(() => s.Request(new OpenCmd { Id = "queued" }, "queued", TimeSpan.FromMilliseconds(40)));
            Assert.False(result.sent);
            release.SetResult();
            await first;
            await Task.Delay(100);
            Assert.DoesNotContain(t.Sent, x => BridgeProtocol.ReadType(x) == "open");
        }

        [Fact]
        public async Task SendStartedButUnconfirmedIsUncertainNotRejected()
        {
            var t = new Transport { Block = ct => Task.Delay(Timeout.Infinite, ct) };
            var s = Session(t);
            var result = await Task.Run(() => s.Request(new OpenCmd { Id = "inflight" }, "inflight", TimeSpan.FromMilliseconds(40)));
            Assert.True(result.sent);
            Assert.Null(result.response);
            Assert.False(s.IsAlive);
        }

        [Fact]
        public async Task TimedOutRuntimeCommandNeverExecutesAfterUnblocking()
        {
            using var r = Runtime();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocker = r.Command(_ => { entered.Set(); release.Wait(); return "released"; });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            int calls = 0;
            var queued = r.Command(_ => { Interlocked.Increment(ref calls); return "too late"; });
            try { await Assert.ThrowsAsync<TimeoutException>(() => queued); }
            finally { release.Set(); }
            try { await blocker; } catch (TimeoutException) { }
            await r.Command(_ => "barrier");
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task DisposedRuntimeRejectsNewCommands()
        {
            var r = Runtime();
            r.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => r.Command(_ => "must not run"));
        }

        [Fact]
        public async Task NewEngineIsStoppedEvenWithLegacyAutoStartEnabled()
        {
            var cfg = Config();
            cfg.Update(c => c.Accounts["cTrader:Pepperstone:910001"] = new AccountSettings { AutoStart = true });
            using var r = Runtime(cfg);
            var s = Session(); Symbols(s); r.Attach(s);
            EngineMode? mode = null;
            await r.Command(e => { mode = e?.Mode; return "mode"; });
            Assert.Equal(EngineMode.Stopped, mode);
        }

        [Fact]
        public async Task EmergencyBeforeEngineReadySurvivesRestart()
        {
            using (var r = Runtime()) await Action(r, "emergency");
            using var restarted = Runtime();
            var s = Session(); Symbols(s); restarted.Attach(s);
            EngineMode? mode = null;
            await restarted.Command(e => { mode = e?.Mode; return "mode"; });
            Assert.Equal(EngineMode.EmergencyStopped, mode);
        }

        [Fact]
        public async Task LiveUnlockCannotOverrideDemoOnlyEvenWithLegacyFlags()
        {
            var cfg = Config();
            cfg.Update(c => { c.LiveTradingFeature = true; c.Accounts["cTrader:Pepperstone:910001"] = new AccountSettings { AllowLive = true, LiveConfirmation = "LIVE 910001 AKCEPTUJE RYZYKO" }; });
            using var r = Runtime(cfg);
            var s = Session(live: true); Symbols(s); r.Attach(s);
            var message = await r.Command(e => r.ExecuteAction(e, "unlockLive", JsonSerializer.SerializeToElement(new { phrase = "LIVE 910001 AKCEPTUJE RYZYKO" })));
            Assert.Contains("wyłączony globalnie", message);
            Assert.StartsWith("Nie można uruchomić", await Action(r, "start"));
        }

        [Fact]
        public async Task StatusIncludesActualAccountAndPositionFields()
        {
            using var r = Runtime();
            var s = Session(); Symbols(s); r.Attach(s);
            await WaitUntil(() => r.Status != null);
            var account = Status(r).GetProperty("account");
            Assert.Equal(12345, account.GetProperty("balance").GetDouble());
            Assert.Equal(500, account.GetProperty("margin").GetDouble());
            Assert.Equal(11500, account.GetProperty("freeMargin").GetDouble());
            Assert.False(account.GetProperty("isLive").GetBoolean());
            Assert.Equal(JsonValueKind.Array, Status(r).GetProperty("positions").ValueKind);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("{\"AccountNumber\":910001,\"Mode\":\"Running\",\"Ledger\":null}")]
        public async Task CorruptedStateNeverBuildsOrOverwritesEvidence(string json)
        {
            string dir = Path.Combine(_dir, "konta", "cTrader_Pepperstone_910001", "stan");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "APEX_APEX_910001.json");
            File.WriteAllText(path, json);
            using var r = Runtime();
            var s = Session(); Symbols(s); r.Attach(s);
            await r.Command(_ => "barrier");
            await WaitUntil(() => r.Status != null);
            Assert.False(Status(r).GetProperty("engineReady").GetBoolean());
            Assert.Equal(json, File.ReadAllText(path));
        }

        [Fact]
        public async Task ConcurrentHelloCannotAcceptTwoOwners()
        {
            var cfg = Config();
            using var m = new AccountManager(cfg, new NewsCalendar(), new AppLog(Path.Combine(_dir, "logs")));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport { Block = _ => { entered.TrySetResult(); return release.Task; } };
            var first = new BridgeSession(transport);
            var second = new BridgeSession(new Transport());
            m.Register(first); m.Register(second);
            string hello = BridgeProtocol.Serialize(new HelloMsg { Key = cfg.Config.PairingKey, Platform = "cTrader", Account = Account() });
            var a = Task.Run(() => first.Handle(hello));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var b = Task.Run(() => second.Handle(hello));
            await Task.Delay(50);
            release.TrySetResult();
            await Task.WhenAll(a, b);
            Assert.Single(new[] { first, second }, s => s.State == SessionState.Active);
            Assert.Single(m.Runtimes);
        }

        [Fact]
        public void StaleSessionIsRetiredBeforeReplacement()
        {
            var cfg = Config();
            using var m = new AccountManager(cfg, new NewsCalendar(), new AppLog(Path.Combine(_dir, "logs")));
            string hello = BridgeProtocol.Serialize(new HelloMsg { Key = cfg.Config.PairingKey, Platform = "cTrader", Account = Account() });
            var first = new BridgeSession(new Transport()); m.Register(first); first.Handle(hello);
            typeof(BridgeSession).GetProperty(nameof(BridgeSession.LastMessageUtc))!.SetValue(first, DateTime.UtcNow.AddMinutes(-1));
            var second = new BridgeSession(new Transport()); m.Register(second); second.Handle(hello);
            Assert.Equal(SessionState.Closed, first.State);
            Assert.Equal(SessionState.Active, second.State);
            m.Unregister(first);
            Assert.Same(second, m.Runtimes.Single().Adapter.Session);
        }

        [Fact]
        public void ManagerCannotCreateRuntimeAfterDisposal()
        {
            var cfg = Config();
            var m = new AccountManager(cfg, new NewsCalendar(), new AppLog(Path.Combine(_dir, "logs")));
            var s = new BridgeSession(new Transport()); m.Register(s);
            m.Dispose();
            s.Handle(BridgeProtocol.Serialize(new HelloMsg { Key = cfg.Config.PairingKey, Platform = "cTrader", Account = Account() }));
            Assert.Empty(m.Runtimes);
            Assert.NotEqual(SessionState.Active, s.State);
            m.Dispose();
        }

        [Fact]
        public void InvalidOptionalTcpPortCannotStopDesktop()
        {
            var cfg = Config(); cfg.Update(c => c.BridgeTcpPort = -1);
            using var m = new AccountManager(cfg, new NewsCalendar(), new AppLog(Path.Combine(_dir, "logs")));
            var b = new BridgeServer(m, cfg, new AppLog(Path.Combine(_dir, "logs")));
            b.StartTcp(CancellationToken.None);
            b.Stop();
        }

        [Theory]
        [InlineData("{")]
        [InlineData("null")]
        [InlineData("{\"Risk\":null}")]
        public void CorruptConfigIsPreservedAndCannotSilentlyReset(string json)
        {
            File.WriteAllText(Path.Combine(_dir, "apex.config.json"), json);
            Assert.Throws<InvalidDataException>(() => Config());
            Assert.Equal(json, File.ReadAllText(Path.Combine(_dir, "apex.config.json")));
        }

        [Fact]
        public async Task StorageFailureBlocksFurtherStarts()
        {
            using var r = Runtime();
            var s = Session(); Symbols(s); r.Attach(s);
            await r.Command(_ => "barrier");
            string path = Path.Combine(_dir, "konta", "cTrader_Pepperstone_910001", "stan", "APEX_APEX_910001.json");
            File.Delete(path); Directory.CreateDirectory(path);
            await r.Command(e => { e!.CmdSetRisk(0.3); e.SaveState(DateTime.UtcNow, true); return "save attempted"; });
            Assert.StartsWith("Nie można uruchomić", await Action(r, "start"));
        }

        [Fact]
        public async Task ResetEmergencyIsDurableAndStillRestartsStopped()
        {
            using (var r = Runtime())
            {
                var s = Session(); Symbols(s); r.Attach(s);
                await Action(r, "emergency");
                await Action(r, "resetEmergency");
            }
            using var restarted = Runtime();
            var session = Session(); Symbols(session); restarted.Attach(session);
            EngineMode? mode = null;
            await restarted.Command(e => { mode = e?.Mode; return "mode"; });
            Assert.Equal(EngineMode.Stopped, mode);
        }

        [Fact]
        public void FileStoreDoesNotSilentlyRestoreStaleBackup()
        {
            var store = new FileStore(Path.Combine(_dir, "state"));
            File.WriteAllText(Path.Combine(_dir, "state", "key.json.bak"), "{\"Mode\":\"Running\"}");
            Assert.ThrowsAny<IOException>(() => store.Get("key"));
        }

        [Fact]
        public void FailedConfigUpdateDoesNotPublishUnsavedSettings()
        {
            var c = Config();
            string oldLabel = c.Config.Label;
            File.Delete(c.Path_);
            Directory.CreateDirectory(c.Path_);
            Assert.ThrowsAny<IOException>(() => c.Update(x => x.Label = "UNSAVED"));
            Assert.Equal(oldLabel, c.Config.Label);
        }

        [Theory]
        [InlineData(19)]
        [InlineData(20)]
        [InlineData(25)]
        public void BrokenJournalRowCannotCrashRestart(int cells)
        {
            string dir = Path.Combine(_dir, "journal"); Directory.CreateDirectory(dir);
            var row = Enumerable.Repeat("0", cells).ToArray();
            row[0] = cells == 25 ? "99999999999999999999999999" : "1";
            row[8] = row[9] = "2026-10-26 12:00:00";
            File.WriteAllText(Path.Combine(dir, "transakcje.csv"), TradeRecord.CsvHeader + "\n" + string.Join(';', row));
            var journal = new FileJournal(dir);
            Assert.Empty(journal.AllRecent());
        }
    }
}
