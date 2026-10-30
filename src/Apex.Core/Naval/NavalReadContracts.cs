using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace Apex.Core.Naval;

// All nested economic records have init-only scalar/record properties. Collection constructors
// always take a private copy, including on JSON deserialization; no writer-owned arrays escape.
public sealed record NavalReadMoney(decimal Amount, string Currency);
public sealed record NavalReadAccount(NavalAccountId Account, NavalReadMoney Seed, NavalReadMoney Cash);
public sealed record NavalReadReservation(NavalReservation Reservation, string BaseCurrency, string QuoteCurrency);
public sealed record NavalReadFill(string Key, NavalFill Fill, string BaseCurrency, string QuoteCurrency);
public sealed record NavalReadFleet
{
    public NavalFleet Fleet { get; }
    public IReadOnlyList<NavalReadReservation> Reservations { get; }
    public IReadOnlyList<NavalReadFill> Fills { get; }
    public NavalReadFleet(NavalFleet fleet, IReadOnlyList<NavalReadReservation> reservations, IReadOnlyList<NavalReadFill> fills)
    { Fleet=fleet; Reservations=Array.AsReadOnly(reservations.ToArray()); Fills=Array.AsReadOnly(fills.ToArray()); }
}
public sealed record NavalReadCheckpoint
{
    public long FinancialRevision { get; }
    public NavalPortfolioId PortfolioId { get; }
    public string ConfigDigest { get; }
    public long SourceGeneration { get; }
    public long SourceSequence { get; }
    public DateTimeOffset? ReconciledUtc { get; }
    public string DurableMode { get; }
    public string ReasonCode { get; }
    public bool DailyLatch { get; }
    public bool DrawdownLatch { get; }
    public bool EmergencyLatch { get; }
    public NavalReadMoney? HistoricalLiquidationEquity { get; }
    public string CurrentQuality => "UNKNOWN";
    public string RuntimeReadiness => "UNKNOWN";
    public string BrokerState => "NOT_CONFIGURED";
    public string Coverage => "UNKNOWN";
    public string? BrokerAccountId => null;
    public NavalReadMoney? BrokerEquity => null;
    public NavalReadMoney? RealizedPnl => null;
    public NavalReadMoney? UnrealizedPnl => null;
    public IReadOnlyList<NavalReadAccount> Accounts { get; }
    public IReadOnlyList<NavalReadFleet> Fleets { get; }
    public NavalReadCheckpoint(long financialRevision, NavalPortfolioId portfolioId, string configDigest,
        long sourceGeneration, long sourceSequence, DateTimeOffset? reconciledUtc, string durableMode, string reasonCode,
        bool dailyLatch, bool drawdownLatch, bool emergencyLatch, NavalReadMoney? historicalLiquidationEquity,
        IReadOnlyList<NavalReadAccount> accounts, IReadOnlyList<NavalReadFleet> fleets)
    {
        FinancialRevision=financialRevision; PortfolioId=portfolioId; ConfigDigest=configDigest;
        SourceGeneration=sourceGeneration; SourceSequence=sourceSequence; ReconciledUtc=reconciledUtc;
        DurableMode=durableMode; ReasonCode=reasonCode; DailyLatch=dailyLatch; DrawdownLatch=drawdownLatch;
        EmergencyLatch=emergencyLatch; HistoricalLiquidationEquity=historicalLiquidationEquity;
        Accounts=Array.AsReadOnly(accounts.ToArray()); Fleets=Array.AsReadOnly(fleets.ToArray());
    }
}
public enum NavalReadFactKind
{
    OfflineReservationAccepted, OfflineReservationRefused, OfflineFillAccepted,
    OfflineEntryCoverageAccepted, OfflineReductionCoverageAccepted, OfflineReconciliationAccepted,
    OfflineSafetyChanged, OfflineModeChanged
}
// Exhaustive tagged payload: only the payload matching Kind is permitted by validation.
public sealed record NavalReadOperation(string ResultCode, string DurableMode, bool DailyLatch, bool DrawdownLatch, bool EmergencyLatch);
public sealed record NavalReadCoverage(bool Complete, bool Final, bool TerminalCoverage, bool ReductionUnsettled, bool OfflineLifecycleFinal);
public sealed record NavalReadFact(NavalReadFactKind Kind, string FactId, NavalAccountId? Account,
    NavalFleet? Fleet, string? ReservationId, string? FillKey, NavalReadReservation? Reservation,
    NavalReadFill? Fill, NavalReadCoverage? Coverage, NavalReadOperation? Operation, NavalEntryIntent? Intent=null);
public sealed record NavalReadBatch
{
    public long Sequence { get; }
    public string EventId { get; }
    public long FinancialRevision { get; }
    public DateTimeOffset? CaptureUtc { get; }
    public DateTimeOffset? ReceiveUtc { get; }
    public DateTimeOffset CommittedUtc { get; }
    public long SourceGeneration { get; }
    public long SourceSequence { get; }
    public IReadOnlyList<NavalReadFact> Facts { get; }
    public NavalReadBatch(long sequence, string eventId, long financialRevision, DateTimeOffset? captureUtc,
        DateTimeOffset? receiveUtc, DateTimeOffset committedUtc, long sourceGeneration, long sourceSequence, IReadOnlyList<NavalReadFact> facts)
    {
        Sequence=sequence; EventId=eventId; FinancialRevision=financialRevision; CaptureUtc=captureUtc;
        ReceiveUtc=receiveUtc; CommittedUtc=committedUtc; SourceGeneration=sourceGeneration; SourceSequence=sourceSequence;
        Facts=Array.AsReadOnly(facts.ToArray());
    }
}
public sealed record NavalReadJournalState
{
    public string Schema { get; }
    public string Epoch { get; }
    public string ScopeId { get; }
    public long BaselineRevision { get; }
    public long LastSequence { get; }
    public long OldestAvailableSequence { get; }
    public NavalReadCheckpoint Checkpoint { get; }
    public IReadOnlyList<NavalReadBatch> Batches { get; }
    public string IntegrityDigest { get; }
    public NavalReadJournalState(string schema, string epoch, string scopeId, long baselineRevision, long lastSequence,
        long oldestAvailableSequence, NavalReadCheckpoint checkpoint, IReadOnlyList<NavalReadBatch> batches, string integrityDigest)
    {
        Schema=schema; Epoch=epoch; ScopeId=scopeId; BaselineRevision=baselineRevision; LastSequence=lastSequence;
        OldestAvailableSequence=oldestAvailableSequence; Checkpoint=checkpoint;
        Batches=Array.AsReadOnly(batches.ToArray()); IntegrityDigest=integrityDigest;
    }
}
public sealed record NavalReadPublication(string Schema, string Classification, NavalReadJournalState Journal,
    long AsOfSequence, long FinancialRevision);

public sealed record NavalCursor(string Epoch, string ScopeId, long Sequence)
{
    public string Encode()
    {
        Validate();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]{"naval-cursor-v1",Epoch,ScopeId,Sequence.ToString(CultureInfo.InvariantCulture)})))
            .TrimEnd('=').Replace('+','-').Replace('/','_');
    }
    public static NavalCursor Decode(string encoded)
    {
        try
        {
            if(string.IsNullOrEmpty(encoded)||encoded.Length>2048||encoded.Any(c=>!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='-'||c=='_')))throw new ArgumentException("Malformed cursor");
            string s=encoded.Replace('-','+').Replace('_','/');s=s.PadRight((s.Length+3)/4*4,'=');
            using var d=JsonDocument.Parse(Convert.FromBase64String(s));var a=d.RootElement;
            if(a.ValueKind!=JsonValueKind.Array||a.GetArrayLength()!=4||a[0].GetString()!="naval-cursor-v1"||
                !long.TryParse(a[3].GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out long n))throw new ArgumentException("Malformed cursor");
            var cursor=new NavalCursor(a[1].GetString()!,a[2].GetString()!,n);cursor.Validate();
            if(cursor.Encode()!=encoded)throw new ArgumentException("Noncanonical cursor");return cursor;
        }
        catch(Exception e) when(e is not OutOfMemoryException){throw new ArgumentException("Malformed cursor",nameof(encoded),e);}
    }
    internal void Validate()
    {
        if(!Guid.TryParseExact(Epoch,"N",out _)||ScopeId==null||ScopeId.Length!=64||ScopeId.Any(c=>!Uri.IsHexDigit(c))||Sequence<0)
            throw new ArgumentException("Malformed cursor tuple");
    }
}
public sealed record NavalReadPage(string Epoch, string ScopeId, long OldestAvailableSequence, long AsOfSequence,
    NavalCursor NextCursor, bool HasMore, bool Gap, bool ResetRequired, IReadOnlyList<NavalReadBatch> Batches);
