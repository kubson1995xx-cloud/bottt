using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;

namespace Apex.Desktop.Naval;
internal sealed record NavalSourceDto(string Kind, DateTimeOffset? SourceClockUtc, DateTimeOffset? CaptureUtc, DateTimeOffset? ReceiveUtc, DateTimeOffset? ReconciledUtc, DateTimeOffset? CommittedUtc, decimal? AgeSeconds, string Quality, string Coverage, string Freshness, bool Stale, string ReasonCode);
internal sealed record NavalBrokerDto(string State = "NOT_CONFIGURED", string? AccountId = null, NavalReadMoney? Balance = null, NavalReadMoney? Equity = null, NavalReadMoney? RealizedPnl = null, NavalReadMoney? UnrealizedPnl = null, object? Statistics = null, string Quality = "UNKNOWN", string Coverage = "UNKNOWN", string RuntimeReadiness = "UNKNOWN");
internal record NavalHostEnvelope
{
    public string SchemaVersion { get; init; } = "naval-host-read-v1";
    public string HostEpoch { get; init; } = "";
    public string Scope { get; init; } = "";
    public string? RequestGeneration { get; init; }
    public DateTimeOffset ServedUtc { get; init; }
    public DateTimeOffset? ObservedUtc { get; init; }
    public string Availability { get; init; } = "UNKNOWN";
    public string ReasonCode { get; init; } = "";
    public string TemporalClassification { get; init; } = "HISTORICAL_OFFLINE_CHECKPOINT";
    public string Classification { get; init; } = "OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY";
    public string? Epoch { get; init; }
    public string? ScopeId { get; init; }
    public long? SourceGeneration { get; init; }
    public long? SourceSequence { get; init; }
    public long? SnapshotVersion { get; init; }
    public long? AsOfSequence { get; init; }
    public long? FinancialRevision { get; init; }
    public IReadOnlyList<NavalSourceDto> Sources { get; init; } = Array.AsReadOnly(Array.Empty<NavalSourceDto>());
    public NavalBrokerDto Broker { get; init; } = new();
}
internal sealed record NavalSnapshotDto : NavalHostEnvelope
{
    public NavalReadCheckpoint? Checkpoint { get; }
    public long? OldestAvailableSequence { get; }
    public string? Cursor { get; }
    internal NavalSnapshotDto(NavalHostEnvelope common, NavalReadPublication? publication) : base(common)
    { Checkpoint = publication?.Journal.Checkpoint; OldestAvailableSequence = publication?.Journal.OldestAvailableSequence; Cursor = publication == null ? null : new NavalCursor(publication.Journal.Epoch, publication.Journal.ScopeId, publication.AsOfSequence).Encode(); }
}
internal sealed record NavalEventsDto : NavalHostEnvelope
{
    public NavalReadPage? Page { get; }
    public string? NextCursorEncoded { get; }
    public bool Gap { get; }
    public bool ResetRequired { get; }
    public IReadOnlyList<NavalReadBatch> Batches { get; }
    internal NavalEventsDto(NavalHostEnvelope common, NavalReadPage? page, bool reset) : base(common)
    { Page = page; NextCursorEncoded = page?.NextCursor.Encode(); Gap = page?.Gap ?? reset; ResetRequired = page?.ResetRequired ?? reset; Batches = page?.Batches ?? Array.AsReadOnly(Array.Empty<NavalReadBatch>()); }
}
internal sealed record NavalSnapshotResult(int StatusCode, NavalSnapshotDto Body);
internal sealed record NavalEventsResult(int StatusCode, NavalEventsDto Body);

// One gate covers capture, acceptance and response construction. No mixed captures and no I/O
// outside the authorized read path. Conflict knowledge is process-local and never persisted.
internal sealed class NavalReadService
{
    private readonly INavalPublicationReader reader;
    private readonly NavalReadScope configuredScope;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NavalPublicationEnvelope? accepted;
    private string? latched;
    private DateTimeOffset? observed;
    internal string HostEpoch { get; } = Guid.NewGuid().ToString("N");
    internal NavalReadScope Scope => configuredScope;
    internal NavalReadService(INavalPublicationReader reader, NavalReadScope configuredScope) { this.reader = reader; this.configuredScope = configuredScope; }
    internal async ValueTask<NavalSnapshotResult> SnapshotAsync(NavalReadScope scope, DateTimeOffset servedUtc, string? requestGeneration, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var read = await Capture(cancellationToken); var common = Envelope(scope, servedUtc, requestGeneration, read);
            return new(read.Availability == NavalPublicationAvailability.Invalid ? 503 : 200, new(common, read.Envelope?.Publication));
        }
        finally { gate.Release(); }
    }
    internal async ValueTask<NavalEventsResult> EventsAsync(NavalReadScope scope, string expectedHostEpoch, NavalCursor cursor, int limit, DateTimeOffset servedUtc, string? requestGeneration, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var read = await Capture(cancellationToken); var common = Envelope(scope, servedUtc, requestGeneration, read);
            if (read.Availability == NavalPublicationAvailability.Invalid) return new(503, new(common, null, true));
            if (read.Availability == NavalPublicationAvailability.NotConfigured) return new(200, new(common with { ReasonCode = "SNAPSHOT_UNAVAILABLE" }, null, true));
            if (expectedHostEpoch != HostEpoch) return new(409, new(common with { ReasonCode = "HOST_EPOCH_CHANGED" }, null, true));
            var page = NavalReadJournal.ReadPage(read.Envelope!.Publication, cursor, limit);
            return new(200, new(common, page, page.ResetRequired));
        }
        finally { gate.Release(); }
    }
    private async ValueTask<NavalPublicationRead> Capture(CancellationToken cancellationToken)
    {
        if (latched != null) return new(NavalPublicationAvailability.Invalid, null, latched);
        var read = await reader.ReadAsync(cancellationToken);
        if (accepted != null && read.ReasonCode == "PUBLICATION_LINEAGE_CHANGED") return Latch("PUBLICATION_LINEAGE_CHANGED");
        if (read.Availability != NavalPublicationAvailability.Available) return read;
        var candidate = read.Envelope!;
        if (accepted != null)
        {
            var old = accepted.Publication; var next = candidate.Publication; var o = old.Journal; var n = next.Journal; var oc = o.Checkpoint; var nc = n.Checkpoint;
            if (o.Epoch != n.Epoch || o.ScopeId != n.ScopeId || oc.PortfolioId != nc.PortfolioId || oc.ConfigDigest != nc.ConfigDigest || !oc.Accounts.Select(a => a.Account).SequenceEqual(nc.Accounts.Select(a => a.Account))) return Latch("PUBLICATION_LINEAGE_CHANGED");
            if (next.FinancialRevision < old.FinancialRevision || next.AsOfSequence < old.AsOfSequence || nc.SourceGeneration < oc.SourceGeneration || (nc.SourceGeneration == oc.SourceGeneration && nc.SourceSequence < oc.SourceSequence)) return Latch("PUBLICATION_BACKWARD");
            if (o.BaselineRevision != n.BaselineRevision) return Latch("PUBLICATION_CONFLICT");
            bool identical = Same(old, next);
            if (next.FinancialRevision == old.FinancialRevision && !identical) return Latch("PUBLICATION_CONFLICT");
            var retained = o.Batches.ToDictionary(b => b.EventId, StringComparer.Ordinal);
            foreach (var b in n.Batches) if (retained.TryGetValue(b.EventId, out var before) && !Same(before, b)) return Latch("PUBLICATION_CONFLICT");
            if (identical) return new(NavalPublicationAvailability.Available, accepted, "HISTORICAL_OFFLINE");
        }
        accepted = candidate; observed = DateTimeOffset.UtcNow;
        return new(NavalPublicationAvailability.Available, accepted, "HISTORICAL_OFFLINE");
    }
    private NavalPublicationRead Latch(string code) { latched = code; return new(NavalPublicationAvailability.Invalid, null, code); }
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, NavalPublicationReader.Json) == JsonSerializer.Serialize(b, NavalPublicationReader.Json);
    private NavalHostEnvelope Envelope(NavalReadScope scope, DateTimeOffset served, string? generation, NavalPublicationRead read)
    {
        var e = read.Envelope; var p = e?.Publication; var c = p?.Journal.Checkpoint;
        DateTimeOffset? capture = p?.Journal.Batches.LastOrDefault(b => b.CaptureUtc != null)?.CaptureUtc;
        DateTimeOffset? receive = p?.Journal.Batches.LastOrDefault(b => b.ReceiveUtc != null)?.ReceiveUtc;
        DateTimeOffset? committed = p?.Journal.Batches.LastOrDefault()?.CommittedUtc;
        bool uncertain = e == null || capture == null || receive == null || committed == null || c?.ReconciledUtc == null || e.SourceClockUtc > served || capture > served || receive > served || committed > served || c?.ReconciledUtc > served;
        if (p != null)
        {
            var batches = p.Journal.Batches;
            for (int i = 1; i < batches.Count; i++) if (batches[i].CommittedUtc < batches[i - 1].CommittedUtc || batches[i].CaptureUtc < batches[i - 1].CaptureUtc || batches[i].ReceiveUtc < batches[i - 1].ReceiveUtc) uncertain = true;
        }
        decimal? age = e == null || uncertain ? null : (decimal)Math.Floor((served - e.SourceClockUtc).TotalSeconds);
        string reason = e == null ? read.ReasonCode : uncertain ? "SOURCE_TIME_UNKNOWN" : "HISTORICAL_OFFLINE";
        return new()
        {
            HostEpoch = HostEpoch,
            Scope = scope.Id,
            RequestGeneration = generation,
            ServedUtc = served,
            ObservedUtc = e == null ? null : observed,
            Availability = read.Availability switch { NavalPublicationAvailability.Available => "AVAILABLE", NavalPublicationAvailability.NotConfigured => "NOT_CONFIGURED", _ => "UNKNOWN" },
            ReasonCode = read.ReasonCode,
            Epoch = p?.Journal.Epoch,
            ScopeId = p?.Journal.ScopeId,
            SourceGeneration = c?.SourceGeneration,
            SourceSequence = c?.SourceSequence,
            SnapshotVersion = p?.FinancialRevision,
            AsOfSequence = p?.AsOfSequence,
            FinancialRevision = p?.FinancialRevision,
            Sources = Array.AsReadOnly(new[] { new NavalSourceDto("OFFLINE_PUBLICATION", e?.SourceClockUtc, capture, receive, c?.ReconciledUtc, committed, age, "UNKNOWN", "UNKNOWN", uncertain ? "UNKNOWN" : "HISTORICAL", true, reason) })
        };
    }
}
