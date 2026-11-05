using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

internal static class NavalBrokerExecutionValidation
{
    // Reuse canonical request, stage and append-only algorithms; these temporary comparison
    // records are NEVER persisted as Offline state and NEVER issue Offline/session authority.
    private static NavalOfflineExecutionBinding WireBinding(NavalBrokerBinding b)=>new(b.Account,b.Account.Environment==NavalEnvironment.OkxDemo?"Demo":"Live",b.Account.AccountId,b.MainUid,b.Account.QuoteCurrency,b.CredentialBindingId,b.ScopeId,b.ConfigDigest,b.SafetyVersion);
    private static NavalExecutionIntent WireIntent(NavalBrokerBinding b,NavalBrokerIntent i,NavalBrokerOperationalState x)=>new()
    {
        IntentId=i.Id,ReservationId=i.ReservationId,Operation=i.Operation,Binding=WireBinding(b),Method="POST",Path=i.Operation switch{NavalExecutionOperation.Submit=>"/api/v5/trade/order",NavalExecutionOperation.Cancel=>"/api/v5/trade/cancel-order",NavalExecutionOperation.CancelProtection=>"/api/v5/trade/cancel-algos",_=>"/api/v5/trade/order-algo"},
        Instrument=i.Instrument,OriginalClientId=i.ClientId,OrderId=i.OrderId,Body=i.Body,RequestDigest=i.RequestDigest,Deadline=i.Deadline,PreparedRevision=i.PreparedRevision,SourceGeneration=i.Generation,SourceSequence=i.Sequence,
        PreparationSessionId=i.Session.ToString("N"),StopEpoch=i.StopEpoch,Stage=i.Stage,DispatchRevision=i.DispatchRevision,Outcome=i.Outcome,
        AllocationKeys=x.IdAllocations.Where(a=>a.ReservationId==i.ReservationId&&a.Ids.Any(id=>id.ClientId==i.ClientId)).Select(a=>a.ExecutionCommandId).ToArray()
    };
    private static EngineState Projection(EngineState e)
    {
        var b=e.NavalBroker!;var x=b.Operational??new();
        return new(){DurableRevision=e.DurableRevision,Naval=e.Naval,NavalExecution=new(){PortfolioId=e.Naval!.Id,FinancialConfigDigest=e.Naval.ConfigDigest,FinancialRevision=e.DurableRevision,
            Binding=WireBinding(b.Baseline.Binding),Namespace=b.Namespace[..16].ToUpperInvariant(),HighWater=b.HighWater,IdAllocations=x.IdAllocations,Intents=x.Intents.Select(i=>WireIntent(b.Baseline.Binding,i,x)).ToArray(),StopEpoch=x.StopEpoch}};
    }
    internal static void Validate(EngineState e)
    {
        try{ValidateCore(e);}catch(Exception failure) when(failure is not OutOfMemoryException){throw NavalBrokerValidation.Invalid();}
    }
    private static void ValidateCore(EngineState e)
    {
        var b=e.NavalBroker!;var x=b.Operational;if(x==null)return;var s=e.Naval!;
        ulong counter=0;var ids=new Dictionary<string,NavalReservedClientId>();var commands=new HashSet<string>();
        if(x.Intents.Count>4096||x.FillEvidence.Count>8192||x.Bills.Count>16384||x.Evidence.Count>1024||x.Obligations.Count>32768)throw NavalBrokerValidation.Invalid();
        foreach(var a in x.IdAllocations)
        {
            if(!commands.Add(a.ExecutionCommandId)||a.Ids.Count!=a.Roles.Count||a.Roles.Count is <1 or >16||a.AllocationDigest!=NavalExecutionValidation.AllocationDigest(a.ReservationId,a.ExecutionCommandId,a.Roles)||!s.Reservations.Any(r=>r.Id==a.ReservationId))throw NavalBrokerValidation.Invalid();
            for(int n=0;n<a.Ids.Count;n++)
            {
                var id=a.Ids[n];counter=checked(counter+1);
                if(id.Counter!=counter||id.ClientId!=NavalExecutionValidation.ClientId(b.Namespace[..16].ToUpperInvariant(),counter)||id.ReservationId!=a.ReservationId||id.ExecutionCommandId!=a.ExecutionCommandId||id.Role!=a.Roles[n]||!ids.TryAdd(id.ClientId,id))throw NavalBrokerValidation.Invalid();
            }
        }
        if(counter!=b.HighWater||x.Intents.Select(i=>i.Id).Distinct().Count()!=x.Intents.Count)throw NavalBrokerValidation.Invalid();
        var placements=new HashSet<string>();
        foreach(var i in x.Intents)
        {
            var r=s.Reservations.SingleOrDefault(r=>r.Id==i.ReservationId);
            if(r==null||i.Instrument!=r.Intent.Instrument||i.Session==Guid.Empty||i.Generation<1||i.Generation>s.Generation||i.Sequence<1||i.Sequence>s.Sequence||i.StopEpoch>x.StopEpoch||i.PreparedRevision<1||i.PreparedRevision>e.DurableRevision||i.Body.Length is <1 or >16384||
                i.Deadline.Offset!=TimeSpan.Zero||i.Deadline.Ticks%TimeSpan.TicksPerMillisecond!=0||!NavalCashRiskPolicy.Text(i.Id)||!ids.TryGetValue(i.ClientId,out var id)||id.ReservationId!=r.Id)throw NavalBrokerValidation.Invalid();
            if(i.Operation==NavalExecutionOperation.Submit&&!placements.Add(i.ClientId))throw NavalBrokerValidation.Invalid();
            if(i.Operation==NavalExecutionOperation.Submit)
            {
                using var request=System.Text.Json.JsonDocument.Parse(i.Body);
                if((request.RootElement.GetProperty("side").GetString()=="sell")!=i.Reduction||id.Role!=NavalClientIdRole.Parent)throw NavalBrokerValidation.Invalid();
            }
            var wire=WireIntent(b.Baseline.Binding,i,x);
            if(i.RequestDigest!=NavalExecutionValidation.RequestDigest(wire))throw NavalBrokerValidation.Invalid();
            NavalExecutionValidation.ValidateBody(wire,ids,placements);NavalExecutionValidation.ValidateStage(wire,e.DurableRevision);
            if(i.Stage==NavalExecutionStage.SubmitUnknown&&!x.SettledIntents.Contains(i.Id)&&(s.Mode!="STOP"||r.Stage!=NavalReservationStage.Uncertain))throw NavalBrokerValidation.Invalid();
        }
        foreach(string settled in x.SettledIntents)
        {
            var i=x.Intents.SingleOrDefault(i=>i.Id==settled);var r=s.Reservations.SingleOrDefault(r=>r.Id==i?.ReservationId);
            if(i==null||i.DispatchRevision==null||r==null||!r.TerminalCoverage)throw NavalBrokerValidation.Invalid();
            if(i.Operation==NavalExecutionOperation.CancelProtection)
            {if(!x.Protection.Any(p=>NavalBrokerSettlement.CanceledProtection(i,p)))throw NavalBrokerValidation.Invalid();continue;}
            if(i.Reduction)
            {if(!x.Orders.Any(o=>NavalBrokerSettlement.ZeroFillCanceled(i,o,x.FillEvidence)||NavalBrokerSettlement.FilledReduction(i,o,x.FillEvidence,x.Bills)))throw NavalBrokerValidation.Invalid();}
            else if(!x.Orders.Any(o=>NavalBrokerSettlement.ZeroFillCanceled(i,o,x.FillEvidence))&&(s.Fills.Where(f=>f.Fill.ReservationId==r.Id&&!f.Fill.Reduction).Sum(f=>f.Fill.Quantity)!=r.Quantity||
                !x.Orders.Any(o=>o.Instrument==i.Instrument&&o.ClientId==i.ClientId&&o.State=="filled"&&o.Accumulated==r.Quantity&&o.Quantity==r.Quantity)))throw NavalBrokerValidation.Invalid();
        }
    }
    internal static void Transition(EngineState old,EngineState next)
    {
        if(old.NavalBroker==null||next.NavalBroker==null)throw NavalBrokerValidation.Invalid();
        NavalExecutionValidation.ValidateTransition(Projection(old),Projection(next));
        var a=old.NavalBroker.Operational;var b=next.NavalBroker.Operational;
        if(a?.DemoAcceptance!=null&&a.DemoAcceptance!=b?.DemoAcceptance)throw NavalBrokerValidation.Invalid();
        if(a?.OrdinaryDemo!=null&&NavalJson.Serialize(a.OrdinaryDemo)!=NavalJson.Serialize(b?.OrdinaryDemo))throw NavalBrokerValidation.Invalid();
        if(a?.OrdinaryCatalog!=null&&NavalJson.Serialize(a.OrdinaryCatalog)!=NavalJson.Serialize(b?.OrdinaryCatalog))throw NavalBrokerValidation.Invalid();
        if(a!=null&&(b==null||a.Signals.Any(p=>!b.Signals.Contains(p))||a.FillEvidence.Any(p=>!b.FillEvidence.Contains(p))||a.Bills.Any(p=>!b.Bills.Contains(p))||a.ExitRequested.Any(p=>!b.ExitRequested.Contains(p))||a.SettledIntents.Any(p=>!b.SettledIntents.Contains(p))))throw NavalBrokerValidation.Invalid();
        if(a!=null&&b!=null)for(int n=0;n<a.Intents.Count;n++)if(a.Intents[n].Reduction!=b.Intents[n].Reduction)throw NavalBrokerValidation.Invalid();
        foreach(var f in old.Naval!.Fills)if(!next.Naval!.Fills.Contains(f))throw NavalBrokerValidation.Invalid();
    }
}
