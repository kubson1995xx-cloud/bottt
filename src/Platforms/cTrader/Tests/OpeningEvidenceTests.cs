using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Apex.Core.Bridge;
using Apex.Core.Model;
using Apex.Platforms.CTrader;
using cAlgo.API;
using cAlgo.API.Collections;
using Xunit;

namespace Apex.Platform.Tests;

public sealed class OpeningEvidenceTests
{
    // Literal wire input exercises the real deserializer, journal, copies and durable restart.
    // Before this extension each of those boundaries silently discarded opening evidence.
    internal const string Wire = """
        {"id":"original-command","status":"filled","positionId":42,"price":4000,
         "openingFill":{"request":{"executionId":"original-command","signalId":"signal","symbol":"US500","direction":1,
           "volumeUnits":2,"stopLossPips":10,"takeProfitPips":20,"label":"fresh-scope","comment":"signal","maxSlippagePips":1},
           "positionId":42,"initialVolumeUnits":1,"volumeStep":0.1,"entryPrice":4000,"entryTimeUtc":"2026-10-26T08:00:00Z",
           "openingDeals":[{"dealId":9223372036854775800,"orderId":17,"positionId":42,"volumeUnits":1,
             "price":4000,"timeUtc":"2026-10-26T08:00:00Z","symbol":"US500","direction":1,"label":"fresh-scope","comment":"signal"}]}}
        """;

    private static ResultMsg Filled() => BridgeProtocol.Deserialize<ResultMsg>(Wire)!;
    private static void Check(ResultMsg result, string currentId)
    {
        Assert.Equal(currentId, result.Id);
        using var json = JsonDocument.Parse(BridgeProtocol.Serialize(result));
        Assert.True(json.RootElement.TryGetProperty("openingFill", out var fill), "Opening fill witness was lost");
        Assert.Equal(1, fill.GetProperty("initialVolumeUnits").GetDouble()); // actual partial fill, NOT requested 2
        Assert.Equal(2, fill.GetProperty("request").GetProperty("volumeUnits").GetDouble());
        Assert.Equal("original-command", fill.GetProperty("request").GetProperty("executionId").GetString());
        Assert.Equal(9223372036854775800L, fill.GetProperty("openingDeals")[0].GetProperty("dealId").GetInt64());
    }

    [Theory]
    [InlineData("filled")]
    [InlineData("uncertain")]
    public void JournalResultAndEveryReplayKeepOriginalFillAndExecutionBinding(string status)
    {
        string saved = "";
        var journal = new CommandJournal(null, x => saved = x);
        var outcome = Filled(); outcome.Status = status;
        var first = journal.Run("original-command", "request-fingerprint", "signal", () => outcome);
        Check(first, "original-command");
        int executions = 0;
        var restored = new CommandJournal(saved, _ => { });
        Check(restored.Run("original-command", "request-fingerprint", "signal", () => { executions++; return Filled(); }), "original-command");
        Check(restored.Run("new-command", "request-fingerprint", "signal", () => { executions++; return Filled(); }), "new-command");
        Assert.Equal(0, executions);
    }

    private static bool Record(CommandJournal journal, string id, ResultMsg result) => journal.RecordOpeningFill(id, result.OpeningFill!);

    [Fact]
    public void CrashAfterOpeningCheckpointBeforeProtectionResultRestartsWithOriginalFillNoSecondOpen()
    {
        string saved = "";
        var journal = new CommandJournal(null, x => saved = x);
        bool recorded = false;
        ResultMsg? replay = null;
        var result = journal.Run("original-command", "request-fingerprint", "signal", () =>
        {
            recorded = Record(journal, "original-command", Filled());
            // This is the exact durable envelope at the point protection/close would execute.
            var crashRestart = new CommandJournal(saved, _ => { });
            replay = crashRestart.Run("new-command", "request-fingerprint", "signal", () => throw new Exception("must never execute"));
            throw new System.IO.IOException("process/protection path failed");
        });
        Assert.True(recorded); Assert.NotNull(replay);
        Assert.Equal("uncertain", replay!.Status); Check(replay, "new-command");
        Assert.Equal("uncertain", result.Status); Check(result, "original-command");
    }

    [Fact]
    public void AmbiguousOpeningCheckpointSaveCannotBecomeFilledOrRetryEvenIfLaterStoreWorks()
    {
        int writes = 0, calls = 0;
        var journal = new CommandJournal(null, _ => { if (++writes == 2) throw new System.IO.IOException("checkpoint publication ambiguous"); });
        bool recorded = true;
        var result = journal.Run("original-command", "request-fingerprint", "signal", () =>
        {
            calls++; recorded = Record(journal, "original-command", Filled()); return Filled();
        });
        Assert.False(recorded);
        Assert.Equal("uncertain", result.Status); Check(result, "original-command");
        var replay = journal.Run("new-command", "request-fingerprint", "signal", () => { calls++; return Filled(); });
        Assert.Equal("uncertain", replay.Status); Check(replay, "new-command");
        Assert.Equal(2, writes); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalResultCannotEraseOrReplaceCheckpointedOriginal(bool conflicting)
    {
        string saved = "";
        var journal = new CommandJournal(null, x => saved = x);
        bool recorded = false;
        var result = journal.Run("original-command", "request-fingerprint", "signal", () =>
        {
            recorded = Record(journal, "original-command", Filled());
            return conflicting ? BridgeProtocol.Deserialize<ResultMsg>(Wire.Replace("\"initialVolumeUnits\":1", "\"initialVolumeUnits\":2"))!
                : new ResultMsg { Id = "original-command", Status = "filled", PositionId = 42, Price = 4000 };
        });
        Assert.True(recorded); Check(result, "original-command");
        Assert.Equal(conflicting ? "uncertain" : "filled", result.Status);
        Check(new CommandJournal(saved, _ => { }).Run("new-command", "request-fingerprint", "signal", () => throw new Exception("no resend")), "new-command");
    }

    [Fact]
    public void FailedResultDurabilityStillReturnsObservedFillWithoutAuthorizingRetry()
    {
        int writes = 0, calls = 0;
        var journal = new CommandJournal(null, _ => { if (++writes == 2) throw new System.IO.IOException("result flush"); });
        var result = journal.Run("original-command", "request-fingerprint", "signal", () => { calls++; return Filled(); });
        Assert.Equal("uncertain", result.Status);
        Check(result, "original-command");
        var repeat = journal.Run("original-command", "request-fingerprint", "signal", () => { calls++; return Filled(); });
        Assert.Equal("uncertain", repeat.Status);
        Check(repeat, "original-command");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FullCloseCopyDoesNotStripOpeningEvidenceFromUncertainProtectionRepair()
    {
        var journal = new CommandJournal(null, _ => { });
        Check(journal.RunFullClose("close", Filled), "close");
    }

    public class Properties : DispatchProxy
    {
        public Dictionary<string, object?> Values = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Values[method!.Name];
    }
    private static T Proxy<T>(Dictionary<string, object?> values) where T : class
    {
        var x = DispatchProxy.Create<T, Properties>(); ((Properties)(object)x).Values = values; return x;
    }
    private static Deal Opening(double volume = 1, long id = 9223372036854775800L, DealStatus status = DealStatus.Filled) => Proxy<Deal>(new()
    {
        ["get_Id"] = id, ["get_OrderId"] = 17, ["get_PositionId"] = 42,
        ["get_PositionImpact"] = DealPositionImpact.Opening, ["get_Status"] = status,
        ["get_VolumeInUnits"] = volume, ["get_ExecutionPrice"] = (double?)4000,
        ["get_SymbolName"] = "US500", ["get_TradeType"] = TradeType.Buy,
        ["get_Label"] = "fresh-scope", ["get_Comment"] = "signal", ["get_ExecutionTime"] = new DateTime(2026, 10, 26, 8, 0, 0),
    });
    private static Position Position(double remaining = 0.5, params Deal[] deals) => Proxy<Position>(new()
    {
        ["get_Id"] = 42, ["get_SymbolName"] = "US500", ["get_TradeType"] = TradeType.Buy,
        ["get_Label"] = "fresh-scope", ["get_Comment"] = "signal", ["get_VolumeInUnits"] = remaining,
        ["get_EntryPrice"] = 4000.0, ["get_EntryTime"] = new DateTime(2026, 10, 26, 8, 0, 0),
        ["get_Deals"] = new ReadonlyList<Deal>(deals),
    });
    private sealed class ReadonlyList<T> : List<T>, IReadonlyList<T>
    {
        public ReadonlyList(IEnumerable<T> values) : base(values) { }
    }
    private static OpeningFillEvidence? Capture(Position position, double step = 0.1) => CTraderOpeningEvidence.Capture(position, new OrderRequest
    {
        Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 2, Label = "fresh-scope", Comment = "signal",
        SignalId = "signal", StopLossPips = 10, TakeProfitPips = 20, MaxSlippagePips = 1,
    }, "original-command", step);

    [Fact]
    public void AvailableClosingReferencesExposeOpeningAdditionEvenWithoutCurrentPosition()
    {
        var type = typeof(OpeningEvidenceTests).Assembly.GetType("Apex.Platforms.CTrader.CTraderScopeEvidence"); Assert.NotNull(type);
        var closing = Proxy<Deal>(new() { ["get_Closing"] = new ReadonlyList<Deal>(new[] { Opening(), Opening(0.5, 45) }) });
        var row = Proxy<HistoricalTrade>(new() { ["get_Label"] = "fresh-scope", ["get_ClosingDeal"] = closing });
        var method = type!.GetMethod("CaptureOpenings")!;
        var observations = (System.Collections.Generic.IReadOnlyList<OpeningDealEvidence>)method.Invoke(null, new object[] { Array.Empty<Position>(), new[] { row }, "fresh-scope" })!;
        Assert.Equal(2, observations.Count); Assert.Contains(observations, x => x.DealId == 45);
    }

    [Fact]
    public void OpeningEvidenceIsFrozenAcrossCallerGraphAndResultMutations()
    {
        var opening = Opening();
        var fill = Capture(Position(0.5, opening))!;
        ((Properties)(object)opening).Values["get_VolumeInUnits"] = 2.0;
        Assert.Equal(1, fill.OpeningDeals[0].VolumeUnits);
        Assert.Throws<NotSupportedException>(() => ((IList<OpeningDealEvidence>)fill.OpeningDeals).Clear());
        var supplied = new List<OpeningDealEvidence>(fill.OpeningDeals);
        var frozen = new OpeningFillEvidence(fill.Request, fill.PositionId, fill.InitialVolumeUnits,
            fill.VolumeStep, fill.EntryPrice, fill.EntryTimeUtc, supplied);
        supplied.Clear();
        Assert.Single(frozen.OpeningDeals);
        var journal = new CommandJournal(null, _ => { });
        var first = journal.Run("original-command", "request-fingerprint", "signal", Filled);
        first.OpeningFill = null; // mutable transport envelope must not mutate the journal entry
        Check(journal.Run("new-command", "request-fingerprint", "signal", () => throw new Exception("no resend")), "new-command");
    }

    [Theory]
    [InlineData("filled")]
    [InlineData("uncertain")]
    public void NativeProtocolRoundTripPreservesOriginalFillAndUncertainPositionIdentity(string status)
    {
        var source = Filled(); source.Status = status;
        var result = ProtocolResult.FromOutcome("new-command", ProtocolResult.ToOutcome(source));
        Assert.Equal(status, result.Status); Assert.Equal(42, result.PositionId);
        Check(result, "new-command");
    }

    [Fact]
    public void NonRoundtrippingQuantumCannotBeRoundedIntoOpeningProof()
    {
        Assert.Null(Capture(Position(0.5, Opening()), Math.BitIncrement(0.1)));
    }

    [Fact]
    public void SdkOpeningGraphProvesOriginalActualFillBeforeFastPartialNotRequestOrRemaining()
    {
        var evidence = Capture(Position(0.5, Opening()));
        Assert.NotNull(evidence);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evidence, BridgeProtocol.Json));
        Assert.Equal(1, json.RootElement.GetProperty("initialVolumeUnits").GetDouble());
        Assert.Equal(2, json.RootElement.GetProperty("request").GetProperty("volumeUnits").GetDouble());
        Assert.Equal(9223372036854775800L, json.RootElement.GetProperty("openingDeals")[0].GetProperty("dealId").GetInt64());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("addition")]
    [InlineData("growing")]
    [InlineData("nonfinite")]
    [InlineData("quantum")]
    [InlineData("partial-status")]
    [InlineData("unresolvable-quantum")]
    [InlineData("entry-time")]
    public void AmbiguousSdkGraphCannotInventOriginalVolume(string failure)
    {
        Position p = failure switch
        {
            "missing" => Position(1),
            "addition" => Position(1, Opening(), Opening(id: 29)),
            "growing" => Position(1.5, Opening()),
            "nonfinite" => Position(1, Opening(double.PositiveInfinity)),
            "partial-status" => Position(1, Opening(status: DealStatus.PartiallyFilled)),
            _ => Position(0.5, Opening()),
        };
        if (failure == "entry-time") ((Properties)(object)p).Values["get_EntryTime"] = new DateTime(2026, 10, 26, 9, 0, 0);
        Assert.Null(Capture(p, failure == "quantum" ? double.NaN : failure == "unresolvable-quantum" ? 1e-18 : 0.1));
    }
}
