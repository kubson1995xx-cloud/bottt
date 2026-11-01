using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

public sealed record NavalBrokerIntent(string Id,string ReservationId,NavalExecutionOperation Operation,string Instrument,
    string ClientId,string? OrderId,byte[] Body,string RequestDigest,DateTimeOffset Deadline,Guid Session,long Generation,long Sequence,
    ulong StopEpoch,long PreparedRevision,NavalExecutionStage Stage,long? DispatchRevision=null,NavalExecutionOutcome? Outcome=null)
{
    public bool Reduction { get; init; }
}
public sealed record NavalBrokerQuote(string Instrument,decimal Bid,decimal Ask,decimal AskQuantity,DateTimeOffset SourceUtc,DateTimeOffset ReceivedUtc,long ReceivedTimestamp);
internal interface INavalBrokerTradingAuthority
{
    Guid SessionId { get; }
    bool SourceCurrent { get; }
    bool CanExecute { get; }
    string? ProtectionProfile { get; }
}
internal sealed partial class NavalBrokerSession
{
    internal EngineState ReadState()=>owner.Read();
    internal void BindTrading(INavalBrokerTradingAuthority authority)=>Coordinator.BindBrokerTrading(this,authority);
    internal NavalRiskResult StartTrading()=>Coordinator.StartBrokerTrading(this);
    internal NavalExecutionIdResult ReserveIds(string reservation,string command,bool reduction=false)=>Coordinator.ReserveBrokerIds(this,reservation,command,reduction);
    internal NavalRiskResult RequestExit(string reservation)=>Coordinator.RequestBrokerExit(this,reservation);
    internal NavalRiskResult Prepare(NavalBrokerIntent intent)=>Coordinator.PrepareBroker(this,intent);
    internal IDisposable? EnterDispatch(string id,string digest,DateTimeOffset now)=>Coordinator.EnterBrokerDispatch(this,id,digest,now);
    internal NavalRiskResult Outcome(string id,NavalExecutionOutcome outcome)=>Coordinator.RecordBrokerOutcome(this,id,outcome);
}
public sealed partial class NavalPortfolioCoordinator
{
    private NavalBrokerSession? _brokerSession;
    private INavalBrokerTradingAuthority? _brokerAuthority;
    private IReadOnlyDictionary<string,NavalBrokerQuote> _brokerQuotes=new Dictionary<string,NavalBrokerQuote>();
    private long _brokerTimestamp;
    internal void BindBrokerTrading(NavalBrokerSession session,INavalBrokerTradingAuthority authority)
    {
        lock(_owner)
        {
            if(!_broker||authority.SessionId==Guid.Empty)throw NavalBrokerValidation.Invalid();
            if(_brokerAuthority!=null)StopCandidate(_owner.Read(),"BROKER_SESSION_REPLACED");
            _brokerSession=session;_brokerAuthority=authority;
        }
    }
    private bool BrokerSource(NavalBrokerSession session)=>_broker&&ReferenceEquals(_brokerSession,session)&&_brokerAuthority?.SourceCurrent==true;
    private bool BrokerFresh()=>_clock is INavalBrokerClock c&&c.Timestamp>=_brokerTimestamp&&(decimal)(c.Timestamp-_brokerTimestamp)/c.TimestampFrequency<30&&
        _brokerAuthority?.SourceCurrent==true&&_brokerQuotes.Count==_definition.Catalog.Count&&_brokerQuotes.Values.All(ValidQuote);
    private bool ValidQuote(NavalBrokerQuote q)=>q.Bid>0&&q.Ask>=q.Bid&&q.AskQuantity>0&&q.SourceUtc<=q.ReceivedUtc&&q.ReceivedUtc<=_clock.UtcNow&&
        _clock.UtcNow-q.SourceUtc<TimeSpan.FromSeconds(5);
    internal NavalRiskResult StartBrokerTrading(NavalBrokerSession session)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;
            if(!BrokerSource(session)||_brokerAuthority?.CanExecute!=true||_brokerAuthority.ProtectionProfile==null||!Fresh(s)||!BrokerFresh()||s.EmergencyLatch||s.DailyLatch||s.DrawdownLatch||
                s.Reservations.Any(r=>r.Stage==NavalReservationStage.Uncertain||ExecutionUnknown(e,r.Id))||e.NavalBroker!.Operational?.Obligations.Count>0)
                return Result(s,NavalMutationStatus.Refused,_brokerAuthority?.ProtectionProfile==null?"PRODUCTION_PROTECTION_PROFILE_UNAPPROVED":"BROKER_START_NOT_PERMITTED");
            var result=Commit(e,s with{Mode="RUNNING",Reason="BROKER_EXPLICIT_START"},NavalMutationStatus.Applied,"BROKER_STARTED",Maintenance());
            if(result.Status==NavalMutationStatus.Applied)_started=true;return result;
        }
    }
    internal NavalRiskResult RequestBrokerExit(NavalBrokerSession session,string reservation)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational??new();
            if(!BrokerSource(session)||_brokerAuthority?.CanExecute!=true||!BrokerFresh()||!s.Reservations.Any(r=>r.Id==reservation&&r.Inventory>0))return Result(s,NavalMutationStatus.Refused,"EXACT_OWNED_EXIT_REQUIRED");
            if(x.ExitRequested.Contains(reservation))return Result(s,NavalMutationStatus.Duplicate,"EXIT_ALREADY_REQUESTED",reservation);
            e.NavalBroker=b with{Operational=x with{ExitRequested=x.ExitRequested.Append(reservation).ToArray()}};
            return StopCandidate(e,"EXACT_OWNED_EXIT_REQUESTED");
        }
    }
    internal NavalExecutionIdResult ReserveBrokerIds(NavalBrokerSession session,string reservation,string command,bool reduction=false)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational??new();
            var old=x.IdAllocations.SingleOrDefault(a=>a.ExecutionCommandId==command);
            if(old!=null)return new(Result(s,NavalMutationStatus.Duplicate,"BROKER_IDS_DUPLICATE",reservation),old.Ids);
            if(!BrokerSource(session)||(!reduction&&!ExecutionReady(s))||_brokerAuthority?.CanExecute!=true||!BrokerFresh()||!s.Reservations.Any(r=>r.Id==reservation&&(!reduction||r.Inventory>0)))return new(Result(s,NavalMutationStatus.Refused,"BROKER_IDS_NOT_READY"),Array.Empty<NavalReservedClientId>());
            var roles=new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo};
            var end=NavalExecutionValidation.CheckedAllocationEnd(b.HighWater,2);
            var ids=roles.Select((role,n)=>new NavalReservedClientId(command,reservation,role,checked(b.HighWater+(ulong)n+1),NavalExecutionValidation.ClientId(b.Namespace[..16].ToUpperInvariant(),checked(b.HighWater+(ulong)n+1)))).ToArray();
            var allocation=new NavalExecutionIdAllocation(command,reservation,roles,ids,NavalExecutionValidation.AllocationDigest(reservation,command,roles));
            e.NavalBroker=b with{HighWater=end,Operational=x with{IdAllocations=x.IdAllocations.Append(allocation).ToArray()}};
            var result=Commit(e,s,NavalMutationStatus.Applied,"BROKER_IDS_RESERVED",Maintenance());return new(result,ids);
        }
    }
    internal NavalRiskResult PrepareBroker(NavalBrokerSession session,NavalBrokerIntent i)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational??new();
            if(x.Intents.Any(p=>p.Id==i.Id))return Result(s,NavalMutationStatus.Duplicate,"BROKER_INTENT_DUPLICATE");
            var r=s.Reservations.SingleOrDefault(r=>r.Id==i.ReservationId);
            bool increasing=i.Operation==NavalExecutionOperation.Submit&&!i.Reduction;
            if(!BrokerSource(session)||(increasing&&!ExecutionReady(s))||_brokerAuthority?.CanExecute!=true||!BrokerFresh()||r==null||i.Session!=_brokerAuthority!.SessionId||i.Generation!=s.Generation||i.Sequence!=s.Sequence||i.StopEpoch!=x.StopEpoch||
                i.Deadline<=_clock.UtcNow||i.PreparedRevision!=s.Revision+1||i.Stage!=NavalExecutionStage.Prepared||i.DispatchRevision!=null||i.Outcome!=null||
                !x.IdAllocations.SelectMany(a=>a.Ids).Any(a=>a.ReservationId==r.Id&&a.ClientId==i.ClientId)||!Enum.IsDefined(typeof(NavalExecutionOperation),i.Operation)||i.Instrument!=r.Intent.Instrument)
                return Result(s,NavalMutationStatus.Refused,"BROKER_PREPARE_DENIED");
            if(!BrokerWorkflow(e,i,r))return Result(s,NavalMutationStatus.Refused,"BROKER_REQUEST_HOLD_MISMATCH");
            e.NavalBroker=b with{Operational=x with{Intents=x.Intents.Append(NavalJson.Clone(i)).ToArray()}};
            return Commit(e,s,NavalMutationStatus.Applied,"BROKER_PREPARED",Maintenance(),r.Id);
        }
    }
    internal IDisposable? EnterBrokerDispatch(NavalBrokerSession session,string id,string digest,DateTimeOffset suppliedNow)
    {
        System.Threading.Monitor.Enter(_owner);bool retained=false;
        try
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational;var i=x?.Intents.SingleOrDefault(i=>i.Id==id);
            if(i==null||!BrokerSource(session)||_brokerAuthority?.CanExecute!=true||((i.Operation==NavalExecutionOperation.Submit&&!i.Reduction)&&(_brokerAuthority.ProtectionProfile==null||!ExecutionReady(s)))||!BrokerFresh()||!BrokerWorkflow(e,i,s.Reservations.Single(r=>r.Id==i.ReservationId))||i.Session!=_brokerAuthority.SessionId||
                i.Stage!=NavalExecutionStage.Prepared||i.RequestDigest!=digest||i.StopEpoch!=x!.StopEpoch||i.Generation!=s.Generation||i.Sequence!=s.Sequence||i.PreparedRevision!=s.Revision||
                _clock.UtcNow>=i.Deadline||suppliedNow>=i.Deadline)return null;
            var dispatched=i with{Stage=NavalExecutionStage.Dispatching,DispatchRevision=checked(s.Revision+1)};
            e.NavalBroker=b with{Operational=x with{Intents=x.Intents.Select(p=>p.Id==id?dispatched:p).ToArray()}};
            if(Commit(e,s,NavalMutationStatus.Applied,"BROKER_DISPATCHING",Maintenance()).Status!=NavalMutationStatus.Applied||!BrokerSource(session)||!BrokerFresh()||_brokerAuthority.CanExecute!=true||_clock.UtcNow>=i.Deadline)return null;
            retained=true;return new DispatchGuard(_owner);
        }
        finally{if(!retained)System.Threading.Monitor.Exit(_owner);}
    }
    internal NavalRiskResult RecordBrokerOutcome(NavalBrokerSession session,string id,NavalExecutionOutcome outcome)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational!;var i=x.Intents.Single(p=>p.Id==id);
            if(!ReferenceEquals(session,_brokerSession))return Result(s,NavalMutationStatus.Refused,"BROKER_SESSION_MISMATCH");
            if(i.Outcome!=null)return Result(s,NavalMutationStatus.Duplicate,"BROKER_OUTCOME_DUPLICATE");
            bool ack=outcome.Disposition==NavalDispatchDisposition.Acknowledged&&outcome.OriginalClientId==i.ClientId&&outcome.OrderId!=null&&outcome.Code=="0"&&outcome.SCode=="0";
            var stage=ack?NavalExecutionStage.Accepted:outcome.Disposition==NavalDispatchDisposition.NotSent&&i.Stage is NavalExecutionStage.Prepared or NavalExecutionStage.Dispatching?NavalExecutionStage.NotSent:NavalExecutionStage.SubmitUnknown;
            e.NavalBroker=b with{Operational=x with{Intents=x.Intents.Select(p=>p.Id==id?i with{Stage=stage,Outcome=outcome}:p).ToArray()}};
            if(stage==NavalExecutionStage.SubmitUnknown){e.Naval=s with{Reservations=s.Reservations.Select(r=>r.Id==i.ReservationId?r with{Stage=NavalReservationStage.Uncertain}:r).ToArray()};return StopCandidate(e,"BROKER_SUBMIT_UNKNOWN");}
            return Commit(e,s,NavalMutationStatus.Applied,"BROKER_OUTCOME",Maintenance(),i.ReservationId);
        }
    }
}
