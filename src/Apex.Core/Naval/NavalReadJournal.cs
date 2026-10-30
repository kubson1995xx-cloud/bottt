using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

internal enum NavalPublicationOperation { Reservation, Evidence, Safety, Mode }
// Deliberately not a receipt: neither OwnerToken nor raw conflicts can enter publication.
internal sealed record NavalPublicationCause(NavalPublicationOperation Operation, string Code, string NewEpoch,
    NavalEvidenceKind? Kind=null, DateTimeOffset? CaptureUtc=null, DateTimeOffset? ReceiveUtc=null,
    long? Generation=null, long? Sequence=null, bool Complete=false, bool Final=false, IReadOnlyList<string>? TerminalIds=null,
    NavalAccountId? Account=null, NavalFleet? Fleet=null, string? ReservationId=null, NavalEntryIntent? Intent=null)
{
    internal static NavalPublicationCause Evidence(NavalEvidenceReceipt r,string epoch)=>new(NavalPublicationOperation.Evidence,"EVIDENCE_APPLIED",epoch,
        r.Kind,r.CaptureUtc,r.ReceiveUtc,r.Generation,r.Sequence,r.Complete,r.Final,Array.AsReadOnly(r.TerminalIds.ToArray()));
}

public static class NavalReadJournal
{
    public const string PublicationSchema="naval-read-publication-v1";
    public const string Classification="OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY";
    private const string JournalSchema="naval-read-journal-v1";
    private const int Capacity=512;
    internal static NavalReadCheckpoint Project(EngineState e,NavalPortfolioDefinition d)
    {
        var s=e.Naval!;
        NavalReadReservation Reservation(NavalReservation r)
        {var i=d.Catalog.Single(i=>i.Instrument==r.Intent.Instrument);return new(r,i.BaseCurrency,i.QuoteCurrency);}
        NavalReadFill Fill(NavalAcceptedFill f)
        {var i=d.Catalog.Single(i=>i.Instrument==f.Fill.Instrument);return new(f.Key,f.Fill,i.BaseCurrency,i.QuoteCurrency);}
        return new(e.DurableRevision,s.Id,s.ConfigDigest,s.Generation,s.Sequence,s.ReconciledUtc,s.Mode,SafeCode(s.Reason),
            s.DailyLatch,s.DrawdownLatch,s.EmergencyLatch,s.LiquidationEquity.HasValue?new(s.LiquidationEquity.Value,s.Id.ReportingCurrency):null,
            s.Accounts.Select(a=>new NavalReadAccount(a.Account,new(a.Seed,a.Account.QuoteCurrency),new(a.Cash,a.Account.QuoteCurrency))).ToArray(),
            new[]{NavalFleet.Alpha,NavalFleet.Bravo}.Select(f=>new NavalReadFleet(f,s.Reservations.Where(r=>r.Fleet==f).Select(Reservation).ToArray(),
                s.Fills.Where(x=>s.Reservations.Any(r=>r.Id==x.Fill.ReservationId&&r.Fleet==f)).Select(Fill).ToArray())).ToArray());
    }
    // Reasons may be caller supplied. Only closed coordinator codes are public, never raw diagnostic text.
    private static string SafeCode(string code)=>KnownCodes.Contains(code)?code:code.StartsWith("UNCERTAIN:",StringComparison.Ordinal)?"UNCERTAIN":"OFFLINE_STOP";
    private static readonly HashSet<string> KnownCodes=new(StringComparer.Ordinal)
    {"INITIAL_STOP","RECONCILED_EXPLICIT_START_REQUIRED","EXPLICIT_OFFLINE_START","EXECUTION_OVERRUN","COMMAND_CONFLICT","RECEIPT_CONFLICT",
     "SOURCE_ORDER_OR_TIME","FINAL_COVERAGE_REQUIRED","FILL_CONFLICT","LATE_FILL_RECONCILE_REQUIRED","UNOWNED_FILL","FEE_OVERRUN","OVERSELL",
     "ENTRY_OVERRUN","CASH_OVERRUN","TERMINAL_CONFLICT","INCOHERENT_MARKS","INCOHERENT_CASH_MARKS","INCOHERENT_ORDER_HOLDINGS","ECONOMIC_OVERFLOW",
     "EVIDENCE_APPLIED","STARTED","RESERVED","SIGNAL_CONSUMED","NOT_RUNNING_FRESH","SIGNAL_TIME","INVALID_LONG_PROTECTION_OR_QUANTUM",
     "EXECUTABLE_PRICE","DECIMAL_OVERFLOW","SYMBOL_OWNED","EXPLICIT_SYNTHETIC_STOP","DAILY_LATCH","DRAWDOWN_LATCH",
     "TRADE_RISK","ACCOUNT_CASH","ACCOUNT_NOTIONAL","CASH_BREACH","LEVERAGE_BREACH","ACCOUNT_NOTIONAL_BREACH","EQUITY_UNKNOWN",
     "SLOT_LIMIT","DAILY_PROJECTED","DRAWDOWN_PROJECTED","CENTRAL_NOTIONAL"};
    internal static NavalReadJournalState Append(EngineState previous,EngineState candidate,NavalPublicationCause cause,DateTimeOffset committedUtc,NavalPortfolioDefinition definition)
    {
        var old=previous.Naval!;var s=candidate.Naval!;var prior=previous.NavalReadJournal;
        long sequence=checked((prior?.LastSequence??0)+1);string epoch=prior?.Epoch??cause.NewEpoch;
        string eventId=epoch+":"+sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var checkpoint=Project(candidate,definition);var facts=new List<NavalReadFact>();
        var reservations=checkpoint.Fleets.SelectMany(x=>x.Reservations).ToDictionary(x=>x.Reservation.Id);
        void Add(NavalReadFactKind kind,NavalReadReservation? reservation=null,NavalReadFill? fill=null,NavalReadCoverage? coverage=null,NavalReadOperation? operation=null)
        {
            var r=reservation?.Reservation??(fill==null?null:s.Reservations.Single(x=>x.Id==fill.Fill.ReservationId));
            facts.Add(new(kind,eventId+":"+facts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),r?.Account??cause.Account,r?.Fleet??cause.Fleet,
                r?.Id??cause.ReservationId,fill?.Key,reservation,fill,coverage,operation,
                cause.Operation==NavalPublicationOperation.Reservation?cause.Intent:null));
        }
        NavalReadOperation Operation()=>new(SafeCode(cause.Code),s.Mode,s.DailyLatch,s.DrawdownLatch,s.EmergencyLatch);
        switch(cause.Operation)
        {
            case NavalPublicationOperation.Reservation:
                Add(cause.ReservationId==null?NavalReadFactKind.OfflineReservationRefused:NavalReadFactKind.OfflineReservationAccepted,
                    cause.ReservationId==null?null:reservations[cause.ReservationId],operation:Operation());break;
            case NavalPublicationOperation.Evidence:
                foreach(var f in checkpoint.Fleets.SelectMany(x=>x.Fills).Where(f=>!old.Fills.Any(o=>o.Key==f.Key)))Add(NavalReadFactKind.OfflineFillAccepted,fill:f);
                if(cause.Complete&&cause.Final)
                    foreach(string id in cause.TerminalIds??Array.Empty<string>())
                    {
                        var r=reservations[id];var before=old.Reservations.Single(x=>x.Id==id);var now=r.Reservation;
                        var coverage=new NavalReadCoverage(true,true,now.TerminalCoverage,now.ReductionUnsettled,
                            now.Pending==0&&now.Inventory==0&&now.TerminalCoverage&&!now.ReductionUnsettled);
                        if(!before.TerminalCoverage)Add(NavalReadFactKind.OfflineEntryCoverageAccepted,r,coverage:coverage);
                        // Reduction coverage requires an actual unsettled reduction, including a reduction in this accepted final batch.
                        if(before.ReductionUnsettled||s.Fills.Any(f=>f.Fill.ReservationId==id&&f.Fill.Reduction&&!old.Fills.Any(o=>o.Key==f.Key)))
                            Add(NavalReadFactKind.OfflineReductionCoverageAccepted,r,coverage:coverage);
                    }
                if(cause.Kind==NavalEvidenceKind.Reconcile)Add(NavalReadFactKind.OfflineReconciliationAccepted,operation:Operation());
                break;
            case NavalPublicationOperation.Safety:Add(NavalReadFactKind.OfflineSafetyChanged,operation:Operation());break;
            case NavalPublicationOperation.Mode:Add(NavalReadFactKind.OfflineModeChanged,operation:Operation());break;
            default:throw new InvalidDataException("Unknown publication cause");
        }
        if(cause.Operation==NavalPublicationOperation.Evidence&&(old.Mode!=s.Mode||old.DailyLatch!=s.DailyLatch||old.DrawdownLatch!=s.DrawdownLatch||old.EmergencyLatch!=s.EmergencyLatch))
            Add(NavalReadFactKind.OfflineSafetyChanged,operation:Operation());
        var batch=new NavalReadBatch(sequence,eventId,candidate.DurableRevision,cause.CaptureUtc,cause.ReceiveUtc,committedUtc,s.Generation,s.Sequence,facts);
        var batches=(prior?.Batches??Array.Empty<NavalReadBatch>()).Append(batch).TakeLast(Capacity).ToArray();
        var j=new NavalReadJournalState(JournalSchema,epoch,Scope(checkpoint),prior?.BaselineRevision??previous.DurableRevision,sequence,batches[0].Sequence,checkpoint,batches,"");
        return WithDigest(j,Digest(j));
    }
    private static NavalReadJournalState WithDigest(NavalReadJournalState j,string digest)=>new(j.Schema,j.Epoch,j.ScopeId,j.BaselineRevision,j.LastSequence,j.OldestAvailableSequence,j.Checkpoint,j.Batches,digest);
    private static string Digest(NavalReadJournalState j)=>NavalJson.Hash(WithDigest(j,""));
    private static string Scope(NavalReadCheckpoint c)=>NavalJson.Hash(new{c.PortfolioId,c.ConfigDigest,Accounts=c.Accounts.Select(x=>x.Account).ToArray()});
    internal static NavalReadPublication? Publication(EngineState e)=>e.NavalReadJournal is {} j?new(PublicationSchema,Classification,j,j.LastSequence,e.DurableRevision):null;
    internal static void Validate(EngineState e,NavalPortfolioDefinition d)
    {
        if(e.NavalReadJournal is not {} j)return;
        ValidateStructure(j);
        if(NavalJson.Serialize(j.Checkpoint)!=NavalJson.Serialize(Project(e,d)))throw new InvalidDataException("Publication checkpoint disagrees with financial revision");
    }
    internal static void ValidateTransition(EngineState previous,EngineState candidate)
    {
        var next=candidate.NavalReadJournal??throw new InvalidDataException("Commit publication missing");
        if(previous.NavalReadJournal is not {} old)
        {if(next.BaselineRevision!=previous.DurableRevision||next.LastSequence!=1)throw new InvalidDataException("Invalid publication baseline");return;}
        if(old.Epoch!=next.Epoch||old.ScopeId!=next.ScopeId||old.BaselineRevision!=next.BaselineRevision||next.LastSequence!=checked(old.LastSequence+1))throw new InvalidDataException("Publication lineage changed");
        foreach(var b in old.Batches.Where(b=>b.Sequence>=next.OldestAvailableSequence))
            if(NavalJson.Serialize(b)!=NavalJson.Serialize(next.Batches.SingleOrDefault(n=>n.Sequence==b.Sequence)))throw new InvalidDataException("Conflicting publication identity");
    }
    private static void ValidateStructure(NavalReadJournalState j)
    {
        if(j.Schema!=JournalSchema||!Guid.TryParseExact(j.Epoch,"N",out _)||j.Checkpoint==null||j.ScopeId!=Scope(j.Checkpoint)||j.BaselineRevision<0||j.LastSequence<1||
            j.LastSequence>long.MaxValue-j.BaselineRevision||j.Checkpoint.FinancialRevision!=j.BaselineRevision+j.LastSequence||
            j.Batches.Count!=Math.Min(Capacity,j.LastSequence)||j.OldestAvailableSequence!=j.LastSequence-j.Batches.Count+1||j.IntegrityDigest!=Digest(j))
            throw new InvalidDataException("Invalid publication metadata");
        long sequence=j.OldestAvailableSequence;long generation=-1,sourceSequence=-1;
        var acceptedFillKeys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var b in j.Batches)
        {
            if(b.Sequence!=sequence++||b.EventId!=j.Epoch+":"+b.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)||b.FinancialRevision!=j.BaselineRevision+b.Sequence||
                b.CommittedUtc==default||b.SourceGeneration<0||b.SourceSequence<0||b.SourceGeneration<generation||
                (b.SourceGeneration==generation&&b.SourceSequence<sourceSequence)||b.CaptureUtc.HasValue!=b.ReceiveUtc.HasValue||b.CaptureUtc>b.ReceiveUtc||b.ReceiveUtc>b.CommittedUtc)
                throw new InvalidDataException("Publication batch discontinuity");
            generation=b.SourceGeneration;sourceSequence=b.SourceSequence;int ordinal=0;
            foreach(var f in b.Facts)
            {
                if(!Enum.IsDefined(typeof(NavalReadFactKind),f.Kind)||f.FactId!=b.EventId+":"+(ordinal++).ToString(System.Globalization.CultureInfo.InvariantCulture))throw new InvalidDataException("Invalid fact identity");
                bool fill=f.Kind==NavalReadFactKind.OfflineFillAccepted;
                bool coverage=f.Kind==NavalReadFactKind.OfflineEntryCoverageAccepted||f.Kind==NavalReadFactKind.OfflineReductionCoverageAccepted;
                bool reservation=f.Kind==NavalReadFactKind.OfflineReservationAccepted||coverage;
                bool operation=!fill&&!coverage;
                bool intent=f.Kind==NavalReadFactKind.OfflineReservationAccepted||f.Kind==NavalReadFactKind.OfflineReservationRefused;
                if((f.Fill!=null)!=fill||(f.Reservation!=null)!=reservation||(f.Coverage!=null)!=coverage||(f.Operation!=null)!=operation||
                    (f.Intent!=null)!=intent||(f.Kind==NavalReadFactKind.OfflineReservationAccepted&&f.Intent!=f.Reservation!.Reservation.Intent)||
                    (fill&&(f.FillKey!=f.Fill!.Key||f.ReservationId!=f.Fill.Fill.ReservationId||f.Account!=f.Fill.Fill.Account||
                        !j.Checkpoint.Fleets.Any(fleet=>fleet.Fleet==f.Fleet&&fleet.Fills.Contains(f.Fill))))||
                    (reservation&&(f.Account!=f.Reservation!.Reservation.Account||f.Fleet!=f.Reservation.Reservation.Fleet||f.ReservationId!=f.Reservation.Reservation.Id))||
                    (coverage&&(!f.Coverage!.Complete||!f.Coverage.Final||!f.Coverage.TerminalCoverage||
                        f.Coverage.TerminalCoverage!=f.Reservation!.Reservation.TerminalCoverage||
                        f.Coverage.ReductionUnsettled!=f.Reservation.Reservation.ReductionUnsettled||
                        f.Coverage.OfflineLifecycleFinal!=(f.Reservation.Reservation.Pending==0&&f.Reservation.Reservation.Inventory==0&&
                            f.Reservation.Reservation.TerminalCoverage&&!f.Reservation.Reservation.ReductionUnsettled))))
                    throw new InvalidDataException("Invalid typed publication fact");
                // FactId only orders publication facts; it cannot make a repeated economic fill new.
                if(fill&&!acceptedFillKeys.Add(f.Fill!.Key))throw new InvalidDataException("Duplicate accepted publication fill");
                if(operation&&((f.Operation!.DurableMode!="STOP"&&f.Operation.DurableMode!="RUNNING")||
                    (!KnownCodes.Contains(f.Operation.ResultCode)&&f.Operation.ResultCode is not ("UNCERTAIN" or "OFFLINE_STOP"))))
                    throw new InvalidDataException("Invalid public operation vocabulary");
            }
        }
        var last=j.Batches.Last();if(last.SourceGeneration!=j.Checkpoint.SourceGeneration||last.SourceSequence!=j.Checkpoint.SourceSequence)throw new InvalidDataException("Publication source mismatch");
    }
    // Check required metadata properties before deserialization can replace absent values with defaults.
    // This is deliberately additional to (not a replacement for) existing financial required-field checks.
    internal static void RequireMetadata(JsonElement e)=>Require(e,typeof(NavalReadJournalState));
    private static void Require(JsonElement e,Type type)
    {
        if(e.ValueKind==JsonValueKind.Null)
        {if(type.IsValueType&&Nullable.GetUnderlyingType(type)==null)throw new InvalidDataException("Null publication value");return;}
        type=Nullable.GetUnderlyingType(type)??type;
        if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(IReadOnlyList<>))
        {foreach(var item in e.EnumerateArray())Require(item,type.GetGenericArguments()[0]);return;}
        if(type==typeof(string)||type.IsValueType)return;
        foreach(var p in type.GetProperties())
        {
            if(!e.TryGetProperty(p.Name,out var value))throw new InvalidDataException("Missing publication field "+p.Name);
            if(type==typeof(NavalReadCheckpoint))
            {
                string? expected=p.Name switch{"CurrentQuality" or "RuntimeReadiness" or "Coverage"=>"UNKNOWN","BrokerState"=>"NOT_CONFIGURED",_=>null};
                if(expected!=null&&(value.ValueKind!=JsonValueKind.String||value.GetString()!=expected))throw new InvalidDataException("Invalid unknown publication semantics");
                if((p.Name is "BrokerAccountId" or "BrokerEquity" or "RealizedPnl" or "UnrealizedPnl")&&value.ValueKind!=JsonValueKind.Null)throw new InvalidDataException("Unsupported broker/statistical publication");
            }
            Require(value,p.PropertyType);
        }
        var names=type.GetProperties().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in e.EnumerateObject())if(!names.Contains(p.Name)||!seen.Add(p.Name))throw new InvalidDataException("Unknown/duplicate publication field");
    }
    public static NavalReadPage ReadPage(NavalReadPublication publication,NavalCursor cursor,int limit=100)
    {
        if(publication==null||cursor==null||limit<1||limit>100)throw new ArgumentException("Invalid publication/cursor/limit");cursor.Validate();
        var j=publication.Journal;bool invalid=false;
        try{ValidateStructure(j);invalid=publication.Schema!=PublicationSchema||publication.Classification!=Classification||publication.AsOfSequence!=j.LastSequence||publication.FinancialRevision!=j.Checkpoint.FinancialRevision;}
        catch(Exception e) when(e is not OutOfMemoryException){invalid=true;}
        bool gap=invalid||cursor.Epoch!=j.Epoch||cursor.ScopeId!=j.ScopeId||cursor.Sequence>j.LastSequence||cursor.Sequence<j.OldestAvailableSequence-1;
        if(gap)return new(j.Epoch,j.ScopeId,j.OldestAvailableSequence,j.LastSequence,cursor,false,true,true,Array.AsReadOnly(Array.Empty<NavalReadBatch>()));
        var batches=j.Batches.Where(b=>b.Sequence>cursor.Sequence).Take(limit).ToArray();var next=cursor with{Sequence=batches.Length==0?cursor.Sequence:batches.Last().Sequence};
        return new(j.Epoch,j.ScopeId,j.OldestAvailableSequence,j.LastSequence,next,next.Sequence<j.LastSequence,false,false,Array.AsReadOnly(batches));
    }
}
