using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

public sealed record NavalHistoricalIntent(string Id,string Digest);
public sealed record NavalPinnedDust(string Reservation,string Instrument,decimal Quantity,decimal Cost);
public sealed record NavalOrdinaryDemoAdmission(string Schema,string Policy,string BaselineDigest,string SourceIntegrityDigest,
    ulong HistoricalHighWater,IReadOnlyList<NavalHistoricalIntent> HistoricalIntents,NavalPinnedDust? Dust,DateTimeOffset PreparedUtc);
internal sealed partial class NavalBrokerSession
{
    internal NavalRiskResult PrepareOrdinaryDemo()=>Coordinator.PrepareOrdinaryDemo(this);
}
internal static class NavalOrdinaryDemoPolicy
{
    internal const string Id="okx-demo-ordinary-current-market-pinned-dust-v1";
    // Separate admission for a Windows-local first-run owner: no history, no dust, no gap.
    // It never applies to the Linux/VPS owner (different sealed premise provenance).
    internal const string FreshLocalId="okx-demo-ordinary-fresh-windows-local-v1";
    internal static readonly string[] FreshLocalInstruments={"BTC-EUR","ETH-EUR","SOL-EUR"};
    internal static bool FreshLocalOrigin(NavalBrokerState b)=>b.Baseline.Observations.OperatorPremise?.Provenance==NavalCurrentMarketFunding.WindowsLocalProvenance;
    internal static bool FreshLocal(NavalBrokerState b)=>b.Operational?.OrdinaryDemo?.Policy==FreshLocalId;
    internal static bool Historical(NavalBrokerOperationalState x,NavalBrokerIntent i)=>x.OrdinaryDemo?.HistoricalIntents.Any(h=>h.Id==i.Id&&h.Digest==NavalJson.Hash(i))==true&&Terminal(x,i);
    internal static bool Terminal(NavalBrokerOperationalState x,NavalBrokerIntent i)
    {
        if(i.DispatchRevision==null)return false;
        if(i.Operation==NavalExecutionOperation.Cancel)
        {
            var parents=x.Intents.Where(p=>p.Operation==NavalExecutionOperation.Submit&&!p.Reduction&&p.ReservationId==i.ReservationId&&p.ClientId==i.ClientId).ToArray();
            return parents.Length==1&&x.SettledIntents.Contains(parents[0].Id)&&x.Orders.GroupBy(o=>(o.Instrument,o.OrderId)).Select(g=>g.Last()).Any(o=>NavalBrokerSettlement.ZeroFillCanceled(parents[0],o,x.FillEvidence));
        }
        return x.SettledIntents.Contains(i.Id);
    }
    internal static bool Pinned(EngineState e,NavalReservation r)=>e.NavalBroker?.Operational?.OrdinaryDemo?.Dust is {} d&&d.Reservation==r.Id&&d.Instrument==r.Intent.Instrument&&d.Quantity==r.Inventory&&d.Cost==r.AllocatedCost&&r.QuarantineMark is >0&&r.Pending==0&&r.TerminalCoverage&&!r.ReductionUnsettled;
    internal static bool Blocking(EngineState e,string obligation)=>e.NavalBroker?.Operational?.OrdinaryDemo?.Dust is not {} d||obligation!="PROTECTION_GAP:"+d.Reservation||!e.Naval!.Reservations.Any(r=>Pinned(e,r));
    internal static void Validate(EngineState e)
    {
        var b=e.NavalBroker!;var x=b.Operational;var a=x?.OrdinaryDemo;
        if(a==null){if(e.Naval!.Reservations.Any(r=>r.QuarantineMark!=null))throw NavalBrokerValidation.Invalid();return;}
        if(a.Policy==FreshLocalId||FreshLocalOrigin(b))
        {
            // Fresh Windows-local admission only; a Windows-local origin can never carry the Linux admission.
            if(a.Policy!=FreshLocalId||!FreshLocalOrigin(b)||a.Dust!=null||a.HistoricalIntents.Count!=0||a.Schema!="ordinary-demo-admission-v1"||a.BaselineDigest!=b.Baseline.Digest||
                a.SourceIntegrityDigest.Length!=64||a.HistoricalHighWater>b.HighWater||b.Baseline.Binding.Account.Environment!=NavalEnvironment.OkxDemo||b.Baseline.Binding.Account.QuoteCurrency!="EUR"||
                b.Baseline.Observations.FiniteAllocation is not {Quote:"EUR"}||e.Naval!.Reservations.Any(r=>r.QuarantineMark!=null))throw NavalBrokerValidation.Invalid();
            return;
        }
        var r=e.Naval!.Reservations.SingleOrDefault(r=>r.Id==a.Dust.Reservation);var spec=b.Baseline.Observations.Catalog.SingleOrDefault(i=>i.Instrument==a.Dust.Instrument);
        if(b.Baseline.Binding.Account.Environment!=NavalEnvironment.OkxDemo||b.Baseline.Observations.FiniteAllocation==null||a.Schema!="ordinary-demo-admission-v1"||a.Policy!=Id||a.BaselineDigest!=b.Baseline.Digest||
            a.SourceIntegrityDigest.Length!=64||a.HistoricalHighWater>b.HighWater||a.HistoricalIntents.Count==0||a.HistoricalIntents.Select(h=>h.Id).Distinct().Count()!=a.HistoricalIntents.Count||
            a.HistoricalIntents.Any(h=>!x!.Intents.Any(i=>i.Id==h.Id&&NavalJson.Hash(i)==h.Digest&&Terminal(x,i)))||r==null||spec?.BaseCurrency!="SOL"||r.Inventory<=0||r.Inventory>=spec.Quantum||r.Inventory>=spec.MinimumQuantity||!Pinned(e,r)||
            e.Naval.Reservations.Any(p=>p.QuarantineMark!=null&&p.Id!=r.Id))throw NavalBrokerValidation.Invalid();
    }
}
public sealed partial class NavalPortfolioCoordinator
{
    internal NavalRiskResult PrepareOrdinaryDemo(NavalBrokerSession session)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational;
            if(!BrokerSource(session)||_brokerAuthority?.OrdinaryDemoPolicy!=true||_brokerAuthority is not INavalFiniteDemoAuthority{FiniteDemo:false}||b.Baseline.Binding.Account.Environment!=NavalEnvironment.OkxDemo||s.Mode!="STOP"||_started||_frozen)
                return Result(s,NavalMutationStatus.Refused,"ORDINARY_DEMO_STOP_AUTHORITY_REQUIRED");
            if(x?.OrdinaryDemo!=null)return Result(s,NavalMutationStatus.Duplicate,"ORDINARY_DEMO_ALREADY_PREPARED");
            var clock=(INavalBrokerClock)_clock;var funding=x?.LastCurrentMarketFunding;var balances=x?.LastBalances;
            if(x==null||b.Baseline.Observations.FiniteAllocation==null||balances==null||funding==null||!BrokerFresh()||x.LastBalanceCaptureUtc is not {} at||clock.UtcNow-at>=TimeSpan.FromSeconds(30)||at>clock.UtcNow||
                !NavalCurrentMarketFunding.ValidateOrdinary(funding,balances,b.Baseline.Binding.Account.AccountId,b.Baseline.Binding.MainUid,b.Baseline.Binding.CredentialBindingId,b.Baseline.Binding.CredentialFingerprint,_brokerAuthority.SessionId,funding.Capture)||
                !NavalCurrentMarketFunding.FreshOwnedRecovery(funding,clock.UtcNow,clock.Timestamp,clock.TimestampFrequency,at,_brokerTimestamp)||s.EmergencyLatch||s.DailyLatch||s.DrawdownLatch||s.Conflicts.Count!=0||
                x.Intents.Any(i=>!NavalOrdinaryDemoPolicy.Terminal(x,i)))return Result(s,NavalMutationStatus.Refused,"ORDINARY_DEMO_CURRENT_SETTLEMENT_REQUIRED");
            if(NavalOrdinaryDemoPolicy.FreshLocalOrigin(b))return PrepareFreshLocal(e,s,b,x,clock);
            var residual=s.Reservations.Where(r=>r.Inventory>0).ToArray();
            if(residual.Length!=1)return Result(s,NavalMutationStatus.Refused,"ORDINARY_DEMO_UNIQUE_HISTORICAL_DUST_REQUIRED");
            var r=residual[0];var spec=_definition.Catalog.Single(i=>i.Instrument==r.Intent.Instrument);
            if(spec.BaseCurrency!="SOL"||r.Inventory>=spec.Quantum||r.Inventory>=spec.MinimumQuantity||r.Pending!=0||!r.TerminalCoverage||r.ReductionUnsettled||
                s.Reservations.Any(p=>p.Pending!=0||p.ReductionUnsettled||!p.TerminalCoverage)||!s.Fills.Any(f=>f.Fill.ReservationId==r.Id&&f.Fill.Reduction)||
                x.Obligations.Count!=1||x.Obligations[0]!="PROTECTION_GAP:"+r.Id||NavalBrokerProtectionOwnership.Competing(e,r).Any(p=>!NavalBrokerProtectionOwnership.Settled(p)))
                return Result(s,NavalMutationStatus.Refused,"ORDINARY_DEMO_PINNED_SOL_PROOF_REQUIRED");
            var admission=new NavalOrdinaryDemoAdmission("ordinary-demo-admission-v1",NavalOrdinaryDemoPolicy.Id,b.Baseline.Digest,b.IntegrityDigest,b.HighWater,
                x.Intents.Select(i=>new NavalHistoricalIntent(i.Id,NavalJson.Hash(i))).ToArray(),new(r.Id,r.Intent.Instrument,r.Inventory,r.AllocatedCost),clock.UtcNow);
            e.NavalBroker=b with{Operational=x with{OrdinaryDemo=admission}};
            s=NavalCashRiskPolicy.Refresh(s with{Reservations=s.Reservations.Select(p=>p.Id==r.Id?p with{QuarantineMark=_brokerQuotes[r.Intent.Instrument].Ask}:p).ToArray()},_definition,clock.UtcNow,false);
            _ready=false;_started=false;_brokerContinuation=null;
            return Commit(e,s with{Mode="STOP",Reason="ORDINARY_DEMO_PREPARED_FRESH_ARM_START_REQUIRED"},NavalMutationStatus.Applied,"ORDINARY_DEMO_PREPARED",Maintenance());
        }
    }
    // Caller holds _owner and has already applied the shared STOP/authority/freshness/settlement gates.
    private NavalRiskResult PrepareFreshLocal(EngineState e,NavalPortfolioState s,NavalBrokerState b,NavalBrokerOperationalState x,INavalBrokerClock clock)
    {
        var account=b.Baseline.Binding.Account;var allocation=b.Baseline.Observations.FiniteAllocation;
        if(s.Fills.Count!=0||s.Reservations.Count!=0||x.Intents.Count!=0||x.SettledIntents.Count!=0||x.Obligations.Count!=0||x.DemoAcceptance!=null||x.OrdinaryCatalog!=null)
            return Result(s,NavalMutationStatus.Refused,"ORDINARY_DEMO_FRESH_LOCAL_HISTORY_PRESENT");
        if(account.Environment!=NavalEnvironment.OkxDemo||account.QuoteCurrency!="EUR"||allocation is not {Quote:"EUR"}||b.Baseline.Observations.Catalog.Count!=1)
            return Result(s,NavalMutationStatus.Refused,"ORDINARY_DEMO_FRESH_LOCAL_SCOPE_INVALID");
        var admission=new NavalOrdinaryDemoAdmission("ordinary-demo-admission-v1",NavalOrdinaryDemoPolicy.FreshLocalId,b.Baseline.Digest,b.IntegrityDigest,b.HighWater,
            Array.Empty<NavalHistoricalIntent>(),null,clock.UtcNow);
        e.NavalBroker=b with{Operational=x with{OrdinaryDemo=admission}};
        _ready=false;_started=false;_brokerContinuation=null;
        return Commit(e,NavalCashRiskPolicy.Refresh(s,_definition,clock.UtcNow,false) with{Mode="STOP",Reason="ORDINARY_DEMO_PREPARED_FRESH_ARM_START_REQUIRED"},NavalMutationStatus.Applied,"ORDINARY_DEMO_PREPARED",Maintenance());
    }
}
