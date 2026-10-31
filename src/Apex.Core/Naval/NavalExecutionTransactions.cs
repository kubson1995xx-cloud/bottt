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
    {_coordinator=coordinator;_binding=NavalJson.Clone(binding);}
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
}

public sealed partial class NavalPortfolioCoordinator
{
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
