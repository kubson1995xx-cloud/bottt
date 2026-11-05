using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

internal sealed record OkxFiniteOrderPlan(decimal AbsoluteMinimum,decimal GrossQuantity,decimal MinimumNet,decimal Sellable,decimal Dust,decimal Price,decimal Debit,decimal Stop,decimal TakeProfit);
internal sealed class OkxFiniteSizingRefusal(OkxFiniteOrderPlan plan):InvalidOperationException("FINITE_MINIMUM_ROUNDTRIP_EXCEEDS_CAP_OR_DEPTH")
{internal OkxFiniteOrderPlan Plan=>plan;}
internal sealed partial class OkxOperationalTradingService
{
    private Func<bool>? finiteSafety;
    private OkxDemoPriceBand? demoBand;
    private readonly System.Collections.Generic.List<object> finitePlanning=new();
    internal object FiniteDiagnostics()=>new{reads=account.ObservationClient?.ReadDiagnostics(),writes=client.ReadDiagnostics(),planning=finitePlanning.ToArray()};
    internal void BindFiniteSafety(Func<bool> safety)
    {if(!account.IsFiniteDemo||finiteSafety!=null)throw new InvalidOperationException("FINITE_DEMO_ONLY");finiteSafety=safety;}
    internal NavalRiskResult PlanFiniteDemo(string instrument,string runId)
    {
        if(!account.IsFiniteDemo||finiteSafety?.Invoke()!=true)return Refuse("FINITE_DEMO_ONLY");
        var now=clock.GetUtcNow();return session.PlanFiniteDemo(new(runId,SessionId,instrument,now,now.AddMinutes(3)));
    }
    internal static OkxFiniteOrderPlan MinimumRoundTrip(NavalInstrument spec,NavalFeeSchedule fees,NavalBrokerQuote quote,bool pending)
    {
        checked
        {
            if(spec.Quantum<=0||spec.MinimumQuantity<=0||spec.TickSize<=0||fees.EntryRate<0||fees.EntryRate>=1||fees.ExitInBase||quote.Bid<=0||quote.Ask<quote.Bid)throw new InvalidOperationException("FINITE_SPEC_OR_FEES_UNSUPPORTED");
            decimal Floor(decimal value,decimal step)=>decimal.Floor(value/step)*step;
            decimal Ceil(decimal value,decimal step)=>decimal.Ceiling(value/step)*step;
            decimal minimum=Ceil(spec.MinimumQuantity,spec.Quantum);
            decimal gross=Ceil(minimum/(fees.EntryInBase?1-fees.EntryRate:1),spec.Quantum);
            decimal net=NavalCashRiskPolicy.NetBuy(gross,fees),sellable=Floor(net,spec.Quantum);
            decimal price=pending?Floor(quote.Bid,spec.TickSize):Ceil(quote.Ask,spec.TickSize);
            decimal debit=NavalCashRiskPolicy.BuyDebit(gross,price,fees);

            // Exactly the smallest gross lot that can close its OWN post-fee inventory.
            if(gross>minimum&&Floor(NavalCashRiskPolicy.NetBuy(gross-spec.Quantum,fees),spec.Quantum)>=minimum)throw new InvalidOperationException("FINITE_MINIMUM_PRECISION_UNPROVEN");
            decimal stop=Floor(price*.99m,spec.TickSize),tp=Ceil(price*1.02m,spec.TickSize);
            if(stop<=0||stop>=price||tp<=price)throw new InvalidOperationException("FINITE_PROTECTION_UNREPRESENTABLE");
            var plan=new OkxFiniteOrderPlan(minimum,gross,net,sellable,net-sellable,price,debit,stop,tp);
            if(sellable<minimum||gross<minimum||gross%spec.Quantum!=0||debit>10||debit<=0||gross>quote.AskQuantity)throw new OkxFiniteSizingRefusal(plan);
            return plan;
        }
    }
    internal async Task<(string Instrument,OkxFiniteOrderPlan Plan)> SelectFiniteInstrumentAsync(CancellationToken ct)
    {
        if(!account.IsFiniteDemo)throw new InvalidOperationException("FINITE_DEMO_ONLY");
        var baseline=session.ReadState().NavalBroker!.Baseline;
        var options=new System.Collections.Generic.List<(string Instrument,OkxFiniteOrderPlan Plan)>();
        foreach(var spec in baseline.Observations.Catalog)
        {
            var quote=await feed.ReadQuoteAsync(spec,ct).ConfigureAwait(false);
            if(!FreshReference(quote))continue;
            try{var candidate=MinimumRoundTrip(spec,baseline.Observations.Fees,quote,false);options.Add((spec.Instrument,candidate));finitePlanning.Add(new{phase="CANDIDATE",spec,fees=baseline.Observations.Fees,quote,plan=candidate});}
            catch(OkxFiniteSizingRefusal e){finitePlanning.Add(new{phase="CANDIDATE_MINIMUM_OVER_CAP_OR_DEPTH",spec,fees=baseline.Observations.Fees,quote,plan=e.Plan});}
            catch(InvalidOperationException){} // An explicitly unsupported/minimum-over-cap candidate, not an unknown fee.
        }
        return options.OrderBy(p=>p.Plan.Debit).ThenBy(p=>p.Instrument,StringComparer.Ordinal).FirstOrDefault() is var selected&&selected.Instrument!=null?selected:throw new InvalidOperationException("NO_ROUNDTRIP_WITHIN_TEN_QUOTE");
    }
    internal async Task<NavalRiskResult> SubmitFiniteDemoAsync(bool pending,CancellationToken ct)
    {
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!account.IsFiniteDemo||!CanExecute||finiteSafety?.Invoke()!=true)return Refuse("FINITE_DEMO_UNARMED");
            var state=session.ReadState();var baseline=state.NavalBroker!.Baseline;var plan=state.NavalBroker.Operational?.DemoAcceptance;
            if(plan==null||plan.Session!=SessionId||clock.GetUtcNow()>=plan.ExpiresUtc)return Refuse("FINITE_DEMO_PLAN_EXPIRED");
            string command=pending?plan.PendingCommand:plan.EntryCommand;
            if(state.Naval!.Commands.Any(c=>c.CommandId==command))return Refuse("FINITE_DEMO_COMMAND_CONSUMED_NO_RETRY");
            var spec=baseline.Observations.Catalog.Single(i=>i.Instrument==plan.Instrument);
            var quote=await feed.ReadQuoteAsync(spec,ct).ConfigureAwait(false);if(!FreshReference(quote))return Refuse("FINITE_QUOTE_STALE");
            var minimum=MinimumRoundTrip(spec,baseline.Observations.Fees,quote,pending);
            finitePlanning.Add(new{phase=pending?"PRE_SEND_PENDING":"PRE_SEND_BUY",spec,fees=baseline.Observations.Fees,quote,plan=minimum});
            demoBand=await feed.ReadPriceBandAsync(spec,ct).ConfigureAwait(false);
            await RefreshLimitReferences(spec,quote,ct).ConfigureAwait(false);
            if(!WithinPrivateMaxima(spec.Instrument,minimum.GrossQuantity,minimum.TakeProfit,true))return Refuse("PRIVATE_MAXIMUM_OR_UNIT_UNPROVEN");
            var marked=session.UpdateQuote(quote);if(marked.Status!=NavalMutationStatus.Applied)return marked;
            var reserve=session.Coordinator.ForFleet(baseline.Binding.Account,NavalFleet.Alpha).TryReserve(new(command,command,spec.Instrument,minimum.GrossQuantity,minimum.Price,minimum.Stop,minimum.TakeProfit));
            if(reserve.Status!=NavalMutationStatus.Applied)return reserve;
            var ids=session.ReserveIds(reserve.ReservationId!,command);if(ids.Result.Status!=NavalMutationStatus.Applied)return ids.Result;
            var protectionRequest=new OkxAttachedProtection(ids.Ids[1].ClientId,N(minimum.TakeProfit),N(minimum.Stop));
            var request=pending?OkxPreparedRequest.FiniteDemoPending(account.ExecutionScope!,spec.Instrument,N(minimum.GrossQuantity),N(minimum.Price),ids.Ids[0].ClientId,Deadline(),protectionRequest):OkxPreparedRequest.Submit(account.ExecutionScope!,spec.Instrument,"buy","fok",N(minimum.GrossQuantity),N(minimum.Price),ids.Ids[0].ClientId,Deadline(),protectionRequest);
            return await DispatchMutation(command,reserve.ReservationId!,request,false,ct).ConfigureAwait(false);
        }
        catch(Exception e) when(e is System.IO.IOException or InvalidOperationException or ArgumentException or OverflowException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {return session.Coordinator.Stop("FINITE_DEMO_ENTRY_REFUSED_NO_RETRY");}
        finally{current=null;lane.Release();}
    }
    private bool FinitePreparedAdmission(OkxPreparedRequest request)
    {
        var state=session.ReadState();var plan=state.NavalBroker!.Operational?.DemoAcceptance;
        if(finiteSafety?.Invoke()!=true||request.Scope.Environment!="Demo"||plan==null||plan.Session!=SessionId||request.Scope.Uid!=state.NavalBroker.Baseline.Binding.Account.AccountId)return false;
        if(request.Operation is OkxExecutionOperation.Cancel or OkxExecutionOperation.CancelProtection)return true; // Core still checks exact ancestry and durable intent.
        if(request.Operation!=OkxExecutionOperation.Submit||demoBand==null||demoBand.Instrument!=request.Instrument||clock.GetUtcNow()<demoBand.SourceUtc||clock.GetUtcNow()-demoBand.SourceUtc>=TimeSpan.FromSeconds(5))return false;
        using var doc=JsonDocument.Parse(request.CopyBody());var row=doc.RootElement;
        decimal price=decimal.Parse(row.GetProperty("px").GetString()!,CultureInfo.InvariantCulture),size=decimal.Parse(row.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture);
        bool buy=row.GetProperty("side").GetString()=="buy";
        if(buy&&(clock.GetUtcNow()>=plan.ExpiresUtc||NavalCashRiskPolicy.BuyDebit(size,price,state.NavalBroker.Baseline.Observations.Fees)>10))return false;
        return !demoBand.Enabled||(buy?demoBand.BuyMaximum is >0&&price<=demoBand.BuyMaximum:demoBand.SellMinimum is >0&&price>=demoBand.SellMinimum);
    }
}
