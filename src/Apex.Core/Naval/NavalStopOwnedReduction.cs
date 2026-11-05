using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

// Cleanup is deliberately incapable of satisfying ordinary execution admission.
internal interface INavalBrokerOwnedReductionAuthority:INavalBrokerStopRecoveryAuthority
{
    bool CanReduce(NavalBrokerIntent original,NavalBrokerIntent mutation,DateTimeOffset now);
}
[JsonConverter(typeof(NavalStopOwnedReductionConverter))]
internal sealed class NavalStopOwnedReduction
{
    internal readonly string ParentId,ReservationId;
    internal readonly Guid Session;
    private NavalStopOwnedReduction(string parent,string reservation,Guid session){ParentId=parent;ReservationId=reservation;Session=session;}
    internal static NavalStopOwnedReduction Issue(string parent,string reservation,Guid session)=>new(parent,reservation,session);
}
internal sealed class NavalStopOwnedReductionConverter:JsonConverter<NavalStopOwnedReduction>
{
    public override NavalStopOwnedReduction Read(ref Utf8JsonReader r,Type t,JsonSerializerOptions o)=>throw new NotSupportedException("Process-local cleanup capability");
    public override void Write(Utf8JsonWriter w,NavalStopOwnedReduction v,JsonSerializerOptions o)=>throw new NotSupportedException("Process-local cleanup capability");
}
internal sealed partial class NavalBrokerSession
{
    internal NavalStopOwnedReduction? OpenOwnedReduction(string parent)=>Coordinator.OpenStopOwnedReduction(this,parent);
    internal NavalRiskResult ApplyOwnedEconomics(NavalStopOwnedReduction cap,NavalBrokerEconomicPass pass)=>Coordinator.ApplyOwnedEconomics(this,cap,pass);
    internal NavalRiskResult RequestOwnedExit(NavalStopOwnedReduction cap)=>Coordinator.RequestStopOwnedExit(this,cap);
    internal NavalExecutionIdResult ReserveOwnedSellId(NavalStopOwnedReduction cap)=>Coordinator.ReserveStopOwnedSellId(this,cap);
    internal NavalRiskResult PrepareOwnedReduction(NavalStopOwnedReduction cap,NavalBrokerIntent intent)=>Coordinator.PrepareStopOwnedReduction(this,cap,intent);
    internal IDisposable? EnterOwnedReduction(NavalStopOwnedReduction cap,string id,string digest,DateTimeOffset now)=>Coordinator.EnterStopOwnedReduction(this,cap,id,digest,now);
}
public sealed partial class NavalPortfolioCoordinator
{
    private NavalStopOwnedReduction? _ownedReduction;
    private long _ownedPassRevision=-1,_ownedPassMono;
    private DateTimeOffset _ownedPassUtc;
    private bool OwnedCapability(NavalBrokerSession session,NavalStopOwnedReduction? cap,EngineState e)=>
        cap!=null&&ReferenceEquals(cap,_ownedReduction)&&cap.Session==_brokerAuthority?.SessionId&&BrokerSource(session)&&!_frozen&&
        _brokerAuthority is INavalBrokerOwnedReductionAuthority{CanExecute:false,ProtectionProfile:null}&&e.Naval!.Mode=="STOP"&&
        e.NavalBroker!.Baseline.Binding.Account.Environment==NavalEnvironment.OkxDemo&&e.NavalBroker.Operational!.Intents.Count(i=>i.Id==cap.ParentId&&i.ReservationId==cap.ReservationId&&!i.Reduction&&i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null)==1;
    internal NavalStopOwnedReduction? OpenStopOwnedReduction(NavalBrokerSession session,string parent)
    {
        lock(_owner)
        {
            var e=_owner.Read();var b=e.NavalBroker!;var x=b.Operational!;var p=x.Intents.SingleOrDefault(i=>i.Id==parent);
            if(_ownedReduction!=null||_frozen||!BrokerSource(session)||_brokerAuthority is not INavalBrokerOwnedReductionAuthority{CanExecute:false,ProtectionProfile:null}||e.Naval!.Mode!="STOP"||
                b.Baseline.Binding.Account.Environment!=NavalEnvironment.OkxDemo||b.Baseline.Binding.Account.QuoteCurrency is not ("EUR" or "USDC")||
                x.DemoAcceptance==null||p==null||p.Operation!=NavalExecutionOperation.Submit||p.Reduction||p.DispatchRevision==null||x.Intents.Count!=1||e.Naval.Reservations.Count!=1||
                x.ExitRequested.Count!=0||x.IdAllocations.Count!=1||b.Baseline.Observations.FiniteAllocation==null||
                b.Baseline.Observations.Account.FeeType is not ("0" or "1")||_definition.Fees.ExitInBase||_definition.Limits.MaxLeverage>1)return null;
            return _ownedReduction=NavalStopOwnedReduction.Issue(p.Id,p.ReservationId,_brokerAuthority.SessionId);
        }
    }
    internal NavalRiskResult ApplyOwnedEconomics(NavalBrokerSession session,NavalStopOwnedReduction cap,NavalBrokerEconomicPass pass)
    {
        lock(_owner)
        {
            var e=_owner.Read();if(!OwnedCapability(session,cap,e))return Result(e.Naval!,NavalMutationStatus.Refused,"OWNED_REDUCTION_CAPABILITY_REQUIRED");
            _ownedPassRevision=-1;
            var result=ApplyBrokerEconomics(session,pass with{OwnedReduction=cap});e=_owner.Read();var x=e.NavalBroker!.Operational!;var r=e.Naval!.Reservations.Single(v=>v.Id==cap.ReservationId);
            if(result.Code is not ("STORAGE_FROZEN" or "COMMIT_CONFLICT")&&pass.CollectionClosed&&OwnedCapability(session,cap,e)&&r.TerminalCoverage&&!r.ReductionUnsettled&&
                x.SettledIntents.Contains(cap.ParentId)&&x.Obligations.All(o=>o=="PROTECTION_GAP:"+cap.ReservationId)&&BrokerFresh())
            {_ownedPassRevision=e.DurableRevision;_ownedPassUtc=pass.CaptureUtc;_ownedPassMono=pass.CaptureTimestamp;}
            return result;
        }
    }
    private bool OwnedReady(NavalBrokerSession session,NavalStopOwnedReduction cap,EngineState e)=>OwnedCapability(session,cap,e)&&_ownedPassRevision==e.DurableRevision&&
        _clock is INavalBrokerClock c&&c.Timestamp>=_ownedPassMono&&c.UtcNow>=_ownedPassUtc&&c.UtcNow-_ownedPassUtc<TimeSpan.FromSeconds(30)&&
        (decimal)(c.Timestamp-_ownedPassMono)/c.TimestampFrequency<30&&BrokerFresh()&&e.NavalBroker!.Operational!.Obligations.All(o=>o=="PROTECTION_GAP:"+cap.ReservationId);
    internal NavalRiskResult RequestStopOwnedExit(NavalBrokerSession session,NavalStopOwnedReduction cap)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalBroker!.Operational!;
            if(!OwnedReady(session,cap,e)||s.Reservations.Single(r=>r.Id==cap.ReservationId).Inventory<=0)return Result(s,NavalMutationStatus.Refused,"OWNED_EXIT_ECONOMIC_COVERAGE_REQUIRED");
            if(x.ExitRequested.Contains(cap.ReservationId))return Result(s,NavalMutationStatus.Duplicate,"OWNED_EXIT_ALREADY_REQUESTED");
            e.NavalBroker=e.NavalBroker with{Operational=x with{ExitRequested=x.ExitRequested.Append(cap.ReservationId).ToArray()}};
            var result=Commit(e,s,NavalMutationStatus.Applied,"STOP_OWNED_EXIT_REQUESTED",Maintenance());if(result.Status==NavalMutationStatus.Applied)_ownedPassRevision=result.DurableRevision;return result;
        }
    }
    internal NavalExecutionIdResult ReserveStopOwnedSellId(NavalBrokerSession session,NavalStopOwnedReduction cap)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational!;
            NavalExecutionIdResult Deny()=>new(Result(s,NavalMutationStatus.Refused,"OWNED_SELL_ID_DENIED_NO_REPLACEMENT"),Array.Empty<NavalReservedClientId>());
            string command="owned-exit-"+cap.ParentId;
            if(!OwnedReady(session,cap,e)||!x.ExitRequested.Contains(cap.ReservationId)||x.Intents.Any(i=>i.Reduction)||x.IdAllocations.Any(a=>a.ExecutionCommandId==command)||
                NavalBrokerProtectionOwnership.Competing(e,s.Reservations.Single(r=>r.Id==cap.ReservationId)).Any(p=>!NavalBrokerProtectionOwnership.Settled(p)))return Deny();
            ulong end=NavalExecutionValidation.CheckedAllocationEnd(b.HighWater,1);var roles=new[]{NavalClientIdRole.Parent};var ids=new[]{new NavalReservedClientId(command,cap.ReservationId,NavalClientIdRole.Parent,end,NavalExecutionValidation.ClientId(b.Namespace[..16].ToUpperInvariant(),end))};
            var allocation=new NavalExecutionIdAllocation(command,cap.ReservationId,roles,ids,NavalExecutionValidation.AllocationDigest(cap.ReservationId,command,roles));
            e.NavalBroker=b with{HighWater=end,Operational=x with{IdAllocations=x.IdAllocations.Append(allocation).ToArray()}};
            var result=Commit(e,s,NavalMutationStatus.Applied,"STOP_OWNED_SELL_ID_RESERVED",Maintenance());if(result.Status==NavalMutationStatus.Applied)_ownedPassRevision=result.DurableRevision;
            return new(result,result.Status==NavalMutationStatus.Applied?ids:Array.Empty<NavalReservedClientId>());
        }
    }
    private bool OwnedMutation(NavalBrokerSession session,NavalStopOwnedReduction cap,EngineState e,NavalBrokerIntent i)
    {
        var s=e.Naval!;var x=e.NavalBroker!.Operational!;var r=s.Reservations.Single(v=>v.Id==cap.ReservationId);var p=x.Intents.Single(v=>v.Id==cap.ParentId);
        if(!OwnedReady(session,cap,e)||i.ReservationId!=r.Id||i.Instrument!=p.Instrument||i.Session!=cap.Session||i.Generation!=s.Generation||i.Sequence!=s.Sequence||i.StopEpoch!=x.StopEpoch||
            !x.ExitRequested.Contains(r.Id)||!r.TerminalCoverage||r.ReductionUnsettled||!((INavalBrokerOwnedReductionAuthority)_brokerAuthority!).CanReduce(p,i,_clock.UtcNow))return false;
        if(i.Operation==NavalExecutionOperation.CancelProtection&&!i.Reduction)
        {
            var cancelSpec=_definition.Catalog.Single(v=>v.Instrument==i.Instrument);
            decimal ownLot=decimal.Floor(r.Inventory/cancelSpec.Quantum)*cancelSpec.Quantum;
            // Only cancel the full-net protection or the exact executable own lot.
            // Raw quantity and PROTECTION_GAP for any dust remain unchanged.
            bool Quantity(decimal? observed)=>observed==r.Inventory||r.Inventory>0&&ownLot>0&&ownLot>=cancelSpec.MinimumQuantity&&
                observed==ownLot&&r.Inventory-ownLot>=0&&r.Inventory-ownLot<cancelSpec.Quantum;
            return i.Id=="owned-cancel-"+cap.ParentId&&!x.Intents.Any(v=>v.Id!=i.Id&&v.Operation==NavalExecutionOperation.CancelProtection)&&
                x.Protection.GroupBy(v=>v.AlgoId).Select(g=>g.Last()).Any(v=>v.Instrument==i.Instrument&&v.AlgoId==i.OrderId&&v.AttachmentClientId==i.ClientId&&v.State=="live"&&Quantity(v.Quantity)&&v.ChildOrderIds.Count==0&&NavalBrokerProtectionOwnership.Reservation(x,v)==r.Id)&&BrokerWorkflow(e,i,r);
        }
        if(i.Operation!=NavalExecutionOperation.Submit||!i.Reduction||i.OrderId!=null||i.Id!="owned-exit-"+cap.ParentId||x.Intents.Any(v=>v.Id!=i.Id&&v.Reduction)||
            !x.IdAllocations.Any(a=>a.ExecutionCommandId==i.Id&&a.ReservationId==r.Id&&a.Ids.Count==1&&a.Ids[0].ClientId==i.ClientId&&a.Ids[0].Role==NavalClientIdRole.Parent)||!BrokerWorkflow(e,i,r))return false;
        using var doc=JsonDocument.Parse(i.Body);var body=doc.RootElement;decimal N(string key)=>decimal.Parse(body.GetProperty(key).GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        var spec=_definition.Catalog.Single(v=>v.Instrument==i.Instrument);var q=_brokerQuotes[i.Instrument];decimal size=N("sz"),price=N("px");
        var own=x.LastBalances?.SingleOrDefault(v=>v.Currency==spec.BaseCurrency);
        return size>=spec.MinimumQuantity&&price==decimal.Floor(q.Bid/spec.TickSize)*spec.TickSize&&price>0&&checked(size*price)<=10&&own?.Available>=size&&own.Frozen==0&&own.OrderFrozen==0;
    }
    internal NavalRiskResult PrepareStopOwnedReduction(NavalBrokerSession session,NavalStopOwnedReduction cap,NavalBrokerIntent i)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalBroker!.Operational!;
            if(x.Intents.Any(p=>p.Id==i.Id))return Result(s,NavalMutationStatus.Refused,"OWNED_MUTATION_ALREADY_PREPARED_NO_RETRY");
            try
            {
                if(!OwnedMutation(session,cap,e,i)||i.Stage!=NavalExecutionStage.Prepared||i.Outcome!=null||i.DispatchRevision!=null||i.PreparedRevision!=s.Revision+1||i.Deadline<=_clock.UtcNow||i.Deadline-_clock.UtcNow>TimeSpan.FromSeconds(10))return Result(s,NavalMutationStatus.Refused,"OWNED_MUTATION_PREPARE_DENIED");
                e.NavalBroker=e.NavalBroker with{Operational=x with{Intents=x.Intents.Append(NavalJson.Clone(i)).ToArray()}};
                // The canonical durable validator, including exact body and digest, is shared.
                var result=Commit(e,s,NavalMutationStatus.Applied,"STOP_OWNED_MUTATION_PREPARED",Maintenance());if(result.Status==NavalMutationStatus.Applied)_ownedPassRevision=result.DurableRevision;return result;
            }
            catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or OverflowException){return Result(s,NavalMutationStatus.Refused,"OWNED_MUTATION_BODY_DENIED");}
        }
    }
    internal IDisposable? EnterStopOwnedReduction(NavalBrokerSession session,NavalStopOwnedReduction cap,string id,string digest,DateTimeOffset suppliedNow)
    {
        System.Threading.Monitor.Enter(_owner);bool held=false;
        try
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalBroker!.Operational!;var i=x.Intents.SingleOrDefault(v=>v.Id==id);
            if(i==null||i.Stage!=NavalExecutionStage.Prepared||i.RequestDigest!=digest||i.PreparedRevision!=s.Revision||suppliedNow>=i.Deadline||_clock.UtcNow>=i.Deadline||!OwnedMutation(session,cap,e,i))return null;
            e.NavalBroker=e.NavalBroker with{Operational=x with{Intents=x.Intents.Select(v=>v.Id==id?v with{Stage=NavalExecutionStage.Dispatching,DispatchRevision=s.Revision+1}:v).ToArray()}};
            var result=Commit(e,s,NavalMutationStatus.Applied,"STOP_OWNED_MUTATION_DISPATCHING",Maintenance());if(result.Status!=NavalMutationStatus.Applied)return null;_ownedPassRevision=result.DurableRevision;
            // Persistence/signing/locks cannot extend either the original observation
            // budget or request expiry. Return while holding the same owner monitor.
            if(_clock.UtcNow>=i.Deadline||!OwnedMutation(session,cap,_owner.Read(),i))return null;
            held=true;return new DispatchGuard(_owner);
        }
        finally{if(!held)System.Threading.Monitor.Exit(_owner);}
    }
}
