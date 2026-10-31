using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

internal sealed record NavalExecutionIdResult(NavalRiskResult Result,IReadOnlyList<NavalReservedClientId> Ids);
// Friend-only Offline composition. This holds the existing coordinator, never another financial owner/store.
internal sealed class NavalExecutionTransactions
{
    private readonly NavalPortfolioCoordinator _coordinator;
    private readonly NavalOfflineExecutionBinding _binding;
    private readonly string _preparationSessionId=Guid.NewGuid().ToString("N");
    private NavalExecutionTransactions(NavalPortfolioCoordinator coordinator,NavalOfflineExecutionBinding binding)
    {_coordinator=coordinator;_binding=NavalJson.Clone(binding);_coordinator.ReplaceOfflineExecutionFacade(_preparationSessionId);}
    internal static NavalExecutionTransactions ForOfflineTests(NavalPortfolioCoordinator coordinator,NavalOfflineExecutionBinding binding)
        =>ForOfflineTests(coordinator,binding,null);
    internal static NavalExecutionTransactions ForOfflineTests(NavalPortfolioCoordinator coordinator,NavalOfflineExecutionBinding binding,string? fixtureNamespace)
    {
        coordinator.InitializeOfflineExecution(binding,fixtureNamespace);
        return new(coordinator,binding);
    }
    internal NavalExecutionIdResult ReserveIds(string reservationId,string executionCommandId,IReadOnlyList<NavalClientIdRole> roles)
        =>_coordinator.ReserveExecutionIds(_binding,reservationId,executionCommandId,roles);
    internal NavalRiskResult Prepare(string reservationId,NavalExecutionIntent intent)
        =>_coordinator.PrepareExecution(_binding,_preparationSessionId,reservationId,intent);
    internal NavalExecutionIntent? Read(string intentId)=>_coordinator.ReadExecution(_binding,intentId);
    internal NavalOfflineExecutionSession OpenOfflineSession()=>_coordinator.OpenOfflineExecutionSession(_preparationSessionId);
    internal IDisposable? TryEnterDispatch(string intentId,string requestDigest,NavalOfflineExecutionSession session,DateTimeOffset utcNow)
        =>_coordinator.EnterOfflineDispatch(_binding,_preparationSessionId,intentId,requestDigest,session,utcNow);
    internal NavalRiskResult ClaimDispatch(string intentId)=>_coordinator.ClaimOfflineDispatch(_binding,_preparationSessionId,intentId);
    internal NavalRiskResult RecordOutcome(string intentId,NavalDispatchOutcome outcome)=>_coordinator.RecordExecutionOutcome(_binding,intentId,outcome);
}
// Opaque in-memory identity. Disk comparison GUIDs cannot reconstruct this object or its owner reference.
[System.Text.Json.Serialization.JsonConverter(typeof(NavalOfflineExecutionSessionConverter))]
internal sealed class NavalOfflineExecutionSession
{
    private readonly Action<NavalOfflineExecutionSession> _revoke;
    private NavalOfflineExecutionSession(Action<NavalOfflineExecutionSession> revoke){_revoke=revoke;}
    internal static NavalOfflineExecutionSession Issue(Action<NavalOfflineExecutionSession> revoke)=>new(revoke);
    internal bool Revoked {get;private set;}
    internal void Invalidate()=>Revoked=true; // coordinator calls only under its owner monitor
    internal void Revoke()=>_revoke(this);
}
internal sealed class NavalOfflineExecutionSessionConverter : System.Text.Json.Serialization.JsonConverter<NavalOfflineExecutionSession>
{
    public override NavalOfflineExecutionSession Read(ref System.Text.Json.Utf8JsonReader reader,Type type,System.Text.Json.JsonSerializerOptions options)=>throw new NotSupportedException("Offline session is process-local");
    public override void Write(System.Text.Json.Utf8JsonWriter writer,NavalOfflineExecutionSession value,System.Text.Json.JsonSerializerOptions options)=>throw new NotSupportedException("Offline session is process-local");
}
internal sealed record NavalDispatchOutcome(long? DispatchRevision,NavalExecutionOutcome Data);

public sealed partial class NavalPortfolioCoordinator
{
    private string? _offlineExecutionEpoch;
    private NavalOfflineExecutionSession? _offlineExecutionSession;
    private readonly HashSet<string> _executionClaims=new(StringComparer.Ordinal);
    private static bool ExecutionUnknown(EngineState e,string reservation)=>e.NavalExecution?.Intents.Any(i=>i.ReservationId==reservation&&i.Stage is NavalExecutionStage.Dispatching or NavalExecutionStage.SubmitUnknown)==true;
    private static bool ExecutionObligation(EngineState e,string reservation)=>e.NavalExecution?.Intents.Any(i=>i.ReservationId==reservation&&i.Stage is NavalExecutionStage.Dispatching or NavalExecutionStage.SubmitUnknown or NavalExecutionStage.Accepted)==true;
    private bool OfflineWorkflowSupported(EngineState e,NavalExecutionIntent i)
    {
        var r=e.Naval!.Reservations.Single(p=>p.Id==i.ReservationId);
        if(i.Operation==NavalExecutionOperation.Cancel)
        {
            var parent=e.NavalExecution!.Intents.SingleOrDefault(p=>p.Operation==NavalExecutionOperation.Submit&&p.OriginalClientId==i.OriginalClientId&&p.ReservationId==i.ReservationId&&p.Instrument==i.Instrument);
            return parent!=null&&parent.DispatchRevision!=null&&(i.OrderId==null||i.OrderId==parent.Outcome?.OrderId);
        }
        if(i.Operation!=NavalExecutionOperation.Submit)return false;
        if(e.NavalExecution!.Intents.Any(p=>p.IntentId!=i.IntentId&&p.ReservationId==i.ReservationId&&p.Operation==NavalExecutionOperation.Submit&&
            (p.Stage!=NavalExecutionStage.Prepared||_executionClaims.Contains(p.IntentId))))return false;
        using var doc=System.Text.Json.JsonDocument.Parse(i.Body);var b=doc.RootElement;
        decimal N(System.Text.Json.JsonElement o,string key)=>decimal.Parse(o.GetProperty(key).GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        return b.GetProperty("side").GetString()=="buy"&&b.GetProperty("ordType").GetString()=="fok"&&N(b,"sz")==r.Quantity&&N(b,"px")==r.Intent.EntryPriceCeiling&&
            b.TryGetProperty("attachAlgoOrds",out var a)&&a.GetArrayLength()==1&&N(a[0],"tpTriggerPx")==r.Intent.TakeProfitPrice&&N(a[0],"slTriggerPx")==r.Intent.StopPrice;
    }
    internal NavalRiskResult ClaimOfflineDispatch(NavalOfflineExecutionBinding binding,string epoch,string id)
    {
        lock(_owner)
        {
            var e=_owner.Read();var i=e.NavalExecution?.Intents.SingleOrDefault(p=>p.IntentId==id);var s=e.Naval!;
            if(_frozen)return Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
            if(i==null||i.Binding!=binding||epoch!=_offlineExecutionEpoch||i.PreparationSessionId!=epoch)return Result(s,NavalMutationStatus.Refused,"EXECUTION_SESSION_MISMATCH");
            if(i.Stage!=NavalExecutionStage.Prepared||_executionClaims.Contains(id))return Result(s,NavalMutationStatus.Duplicate,"EXECUTION_ALREADY_CLAIMED");
            if(!OfflineWorkflowSupported(e,i))return Result(s,NavalMutationStatus.Refused,"EXECUTION_WORKFLOW_UNSUPPORTED");
            _executionClaims.Add(id);return Result(s,NavalMutationStatus.Applied,"EXECUTION_CLAIMED");
        }
    }
    internal void ReplaceOfflineExecutionFacade(string epoch)
    {
        lock(_owner)
        {
            if(_offlineExecutionSession is {Revoked:false})
            { _offlineExecutionSession.Invalidate();StopCandidate(_owner.Read(),"OFFLINE_STOP"); }
            _offlineExecutionEpoch=epoch;_offlineExecutionSession=null;
        }
    }
    internal NavalOfflineExecutionSession OpenOfflineExecutionSession(string epoch)
    {
        lock(_owner)
        {
            var e=_owner.Read();
            if(epoch!=_offlineExecutionEpoch||e.NavalExecution==null||!ExecutionReady(e.Naval!))throw NavalExecutionValidation.Invalid();
            if(_offlineExecutionSession!=null)
            {if(_offlineExecutionSession.Revoked)throw NavalExecutionValidation.Invalid();return _offlineExecutionSession;}
            return _offlineExecutionSession=NavalOfflineExecutionSession.Issue(RevokeOfflineExecutionSession);
        }
    }
    private void RevokeOfflineExecutionSession(NavalOfflineExecutionSession session)
    {
        lock(_owner)
        {
            if(session.Revoked)return;
            session.Invalidate();
            if(ReferenceEquals(session,_offlineExecutionSession))StopCandidate(_owner.Read(),"OFFLINE_STOP");
        }
    }
    private sealed class DispatchGuard : IDisposable
    {
        private object? _lane;
        private readonly int _thread=System.Threading.Thread.CurrentThread.ManagedThreadId;
        internal DispatchGuard(object lane){_lane=lane;}
        public void Dispose()
        {
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=_thread)throw new InvalidOperationException("Dispatch guard must be disposed synchronously");
            if(_lane!=null){var lane=_lane;_lane=null;System.Threading.Monitor.Exit(lane);}
        }
    }
    internal IDisposable? EnterOfflineDispatch(NavalOfflineExecutionBinding binding,string epoch,string id,string digest,NavalOfflineExecutionSession session,DateTimeOffset suppliedNow)
    {
        System.Threading.Monitor.Enter(_owner);bool retained=false;
        try
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalExecution;
            var i=x?.Intents.SingleOrDefault(i=>i.IntentId==id);var r=s.Reservations.SingleOrDefault(r=>r.Id==i?.ReservationId);
            if(x==null||x.Binding!=binding||i==null||r==null||!ExecutionReady(s)||epoch!=_offlineExecutionEpoch||
                !ReferenceEquals(session,_offlineExecutionSession)||session.Revoked||i.PreparationSessionId!=epoch||!_executionClaims.Contains(id)||!OfflineWorkflowSupported(e,i)||
                i.Stage!=NavalExecutionStage.Prepared||i.RequestDigest!=digest||i.StopEpoch!=x.StopEpoch||
                i.PreparedRevision!=e.DurableRevision||i.SourceGeneration!=s.Generation||i.SourceSequence!=s.Sequence||
                i.DefinitionDigest!=_definition.Digest||i.CatalogDigest!=NavalJson.Hash(_definition.Catalog)||i.ReservationDigest!=NavalJson.Hash(r)||
                suppliedNow>=i.Deadline||_clock.UtcNow>=i.Deadline||!NavalCashRiskPolicy.Active(r))return null;
            var dispatch=i with{Stage=NavalExecutionStage.Dispatching,DispatchRevision=checked(e.DurableRevision+1)};
            e.NavalExecution=x with{Intents=x.Intents.Select(old=>old.IntentId==id?dispatch:old).ToArray()};
            if(Commit(e,s,NavalMutationStatus.Applied,"EXECUTION_DISPATCHING",Maintenance()).Status!=NavalMutationStatus.Applied)return null;
            // Persistence may advance time; the caller's captured utcNow never extends authority.
            if(_clock.UtcNow>=i.Deadline||!ExecutionReady(_owner.Read().Naval!)||session.Revoked)return null;
            retained=true;return new DispatchGuard(_owner);
        }
        finally{if(!retained)System.Threading.Monitor.Exit(_owner);}
    }
    internal NavalRiskResult RecordExecutionOutcome(NavalOfflineExecutionBinding binding,string id,NavalDispatchOutcome outcome)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalExecution;var i=x?.Intents.SingleOrDefault(i=>i.IntentId==id);
            if(_frozen)return Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
            if(x==null||x.Binding!=binding||i==null)return Result(s,NavalMutationStatus.Refused,"EXECUTION_IDENTITY");
            if(i.Stage is NavalExecutionStage.Accepted or NavalExecutionStage.SubmitUnknown or NavalExecutionStage.NotSent)
                return Result(s,NavalMutationStatus.Duplicate,"EXECUTION_OUTCOME_DUPLICATE");
            if(i.DispatchRevision!=outcome.DispatchRevision||i.Stage==NavalExecutionStage.Prepared&&outcome.Data.Disposition!=NavalDispatchDisposition.NotSent)
                return Result(s,NavalMutationStatus.Refused,"EXECUTION_ATTEMPT_MISMATCH");
            var o=outcome.Data;
            if(!NavalExecutionValidation.ValidOutcome(i,o))return Result(s,NavalMutationStatus.Refused,"EXECUTION_INVALID_OUTCOME");
            var stage=o.Disposition switch{NavalDispatchDisposition.Acknowledged=>NavalExecutionStage.Accepted,NavalDispatchDisposition.NotSent=>NavalExecutionStage.NotSent,_=>NavalExecutionStage.SubmitUnknown};
            e.NavalExecution=x with{Intents=x.Intents.Select(old=>old.IntentId==id?i with{Stage=stage,Outcome=NavalJson.Clone(o)}:old).ToArray()};
            if(stage==NavalExecutionStage.SubmitUnknown)
            {
                e.Naval=s with{Reservations=s.Reservations.Select(r=>r.Id==i.ReservationId&&NavalCashRiskPolicy.Active(r)?r with{Stage=NavalReservationStage.Uncertain}:r).ToArray()};
                return StopCandidate(e,"UNCERTAIN");
            }
            return Commit(e,s,NavalMutationStatus.Applied,"EXECUTION_OUTCOME",Maintenance(),i.ReservationId);
        }
    }
    internal void InitializeOfflineExecution(NavalOfflineExecutionBinding binding,string? fixtureNamespace)
    {
        lock(_owner)
        {
            if(_frozen)throw NavalExecutionValidation.Invalid();
            NavalExecutionValidation.ValidateBinding(binding,_definition);var e=_owner.Read();
            if(e.NavalExecution is {} existing)
            {if(existing.Binding!=binding||(fixtureNamespace!=null&&existing.Namespace!=fixtureNamespace))throw NavalExecutionValidation.Invalid();return;}
            string ns=fixtureNamespace??Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            if(!NavalExecutionValidation.Match(ns,"[A-F0-9]{16}"))throw NavalExecutionValidation.Invalid();
            e.NavalExecution=new(){PortfolioId=_definition.Id,FinancialConfigDigest=_definition.Digest,Binding=NavalJson.Clone(binding),Namespace=ns};
            var result=Commit(e,e.Naval!,NavalMutationStatus.Applied,"EXECUTION_INITIALIZED",Maintenance());
            if(result.Status!=NavalMutationStatus.Applied)throw NavalExecutionValidation.Invalid();
        }
    }
    private NavalPublicationCause Maintenance()=>new(NavalPublicationOperation.ExecutionMaintenance,"EXECUTION_MAINTENANCE",_publicationEpoch);
    private bool ExecutionReady(NavalPortfolioState s)=>!_frozen&&_started&&s.Mode=="RUNNING"&&Fresh(s)&&!s.DailyLatch&&!s.DrawdownLatch&&!s.EmergencyLatch;
    internal NavalExecutionIntent? ReadExecution(NavalOfflineExecutionBinding binding,string id)
    {
        lock(_owner)
        {var x=_owner.Read().NavalExecution;if(x==null||x.Binding!=binding)throw NavalExecutionValidation.Invalid();return NavalJson.Clone(x.Intents.SingleOrDefault(i=>i.IntentId==id));}
    }
    internal NavalExecutionIdResult ReserveExecutionIds(NavalOfflineExecutionBinding binding,string reservationId,string command,IReadOnlyList<NavalClientIdRole> roles)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalExecution;
            NavalExecutionIdResult Denied(string code)=>new(Result(s,NavalMutationStatus.Refused,code),Array.Empty<NavalReservedClientId>());
            if(_frozen)return new(Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN"),Array.Empty<NavalReservedClientId>());
            if(x==null||x.Binding!=binding||!NavalCashRiskPolicy.Text(command)||roles==null||roles.Count<1||roles.Count>16||roles.Any(r=>!Enum.IsDefined(typeof(NavalClientIdRole),r)))return Denied("EXECUTION_IDENTITY");
            roles=roles.ToArray();string digest=NavalExecutionValidation.AllocationDigest(reservationId,command,roles);
            var previous=x.IdAllocations.SingleOrDefault(a=>a.ExecutionCommandId==command);
            if(previous!=null)
            {
                if(previous.AllocationDigest!=digest)return new(StopCandidate(e,"EXECUTION_ID_CONFLICT",true),Array.Empty<NavalReservedClientId>());
                return new(Result(s,NavalMutationStatus.Duplicate,"EXECUTION_IDS_DUPLICATE",reservationId),NavalJson.Clone(previous.Ids));
            }
            var r=s.Reservations.SingleOrDefault(r=>r.Id==reservationId&&r.Account==binding.Account);
            if(!ExecutionReady(s)||r==null||!NavalCashRiskPolicy.Active(r)||r.TerminalCoverage||r.ReductionUnsettled||r.Stage==NavalReservationStage.Uncertain)return Denied("EXECUTION_NOT_READY");
            ulong end;try{end=NavalExecutionValidation.CheckedAllocationEnd(x.HighWater,roles.Count);}catch(OverflowException){return Denied("EXECUTION_ID_EXHAUSTED");}
            var ids=roles.Select((role,n)=>{ulong counter=checked(x.HighWater+(ulong)n+1);return new NavalReservedClientId(command,reservationId,role,counter,NavalExecutionValidation.ClientId(x.Namespace,counter));}).ToArray();
            if(ids.Any(id=>x.IdAllocations.SelectMany(a=>a.Ids).Any(old=>old.ClientId==id.ClientId)))return Denied("EXECUTION_ID_COLLISION");
            var allocation=new NavalExecutionIdAllocation(command,reservationId,roles,ids,digest);
            e.NavalExecution=x with{HighWater=end,IdAllocations=x.IdAllocations.Append(allocation).ToArray()};
            var result=Commit(e,s,NavalMutationStatus.Applied,"EXECUTION_IDS_RESERVED",Maintenance(),reservationId);
            return new(result,result.Status==NavalMutationStatus.Applied?NavalJson.Clone(ids):Array.Empty<NavalReservedClientId>());
        }
    }
    internal NavalRiskResult PrepareExecution(NavalOfflineExecutionBinding binding,string sessionId,string reservationId,NavalExecutionIntent input)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalExecution;
            if(_frozen)return Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
            if(input==null||x==null||x.Binding!=binding)return Result(s,NavalMutationStatus.Refused,"EXECUTION_IDENTITY");
            input=NavalJson.Clone(input);
            var previous=x.Intents.SingleOrDefault(i=>i.IntentId==input.IntentId);
            if(previous!=null)
            {
                var replay=input with{PreparationSessionId=string.IsNullOrEmpty(input.PreparationSessionId)?previous.PreparationSessionId:input.PreparationSessionId};
                if(reservationId!=previous.ReservationId||NavalJson.Serialize(replay)!=NavalJson.Serialize(previous))return StopCandidate(e,"EXECUTION_INTENT_CONFLICT",true);
                return Result(s,NavalMutationStatus.Duplicate,"EXECUTION_PREPARED_DUPLICATE",reservationId);
            }
            var r=s.Reservations.SingleOrDefault(r=>r.Id==reservationId&&r.Account==binding.Account);
            if(!ExecutionReady(s)||r==null||!NavalCashRiskPolicy.Active(r)||r.TerminalCoverage||r.ReductionUnsettled||r.Stage==NavalReservationStage.Uncertain)
                return Result(s,NavalMutationStatus.Refused,"EXECUTION_NOT_READY");
            if(input.ReservationId!=r.Id||input.CommandId!=r.Intent.CommandId||input.SignalId!=r.Intent.SignalId||input.Binding!=binding||input.PreparedRevision!=checked(e.DurableRevision+1)||
                input.ReservationDigest!=NavalJson.Hash(r)||input.DefinitionDigest!=_definition.Digest||input.CatalogDigest!=NavalJson.Hash(_definition.Catalog)||
                input.SourceGeneration!=s.Generation||input.SourceSequence!=s.Sequence||input.StopEpoch!=x.StopEpoch||
                (!string.IsNullOrEmpty(input.PreparationSessionId)&&input.PreparationSessionId!=sessionId)||input.Deadline<=_clock.UtcNow||
                input.Stage!=NavalExecutionStage.Prepared||input.DispatchRevision!=null||input.Outcome!=null)
                return Result(s,NavalMutationStatus.Refused,"EXECUTION_STALE_OR_MALFORMED");
            var intent=input with{PreparationSessionId=sessionId,PreparedRevision=checked(e.DurableRevision+1),ReservationDigest=NavalJson.Hash(r),
                DefinitionDigest=_definition.Digest,CatalogDigest=NavalJson.Hash(_definition.Catalog),SourceGeneration=s.Generation,SourceSequence=s.Sequence,StopEpoch=x.StopEpoch};
            e.NavalExecution=x with{Intents=x.Intents.Append(intent).ToArray()};
            try
            {
                var validation=NavalJson.Clone(e);validation.DurableRevision=checked(e.DurableRevision+1);
                validation.Naval=s with{Revision=validation.DurableRevision};
                validation.NavalExecution=NavalExecutionValidation.Seal(e.NavalExecution,validation.DurableRevision);
                NavalExecutionValidation.Validate(validation,_definition);
            }
            catch(Exception ex) when(ex is not OutOfMemoryException){return Result(s,NavalMutationStatus.Refused,"EXECUTION_INVALID_REQUEST");}
            return Commit(e,s,NavalMutationStatus.Applied,"EXECUTION_PREPARED",Maintenance(),r.Id);
        }
    }
}
