using System;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
namespace Apex.Core.Naval;
public sealed partial class NavalPortfolioCoordinator
{
    private bool BrokerWorkflow(EngineState e,NavalBrokerIntent i,NavalReservation r)
    {
        var x=e.NavalBroker!.Operational!;
        if(i.Operation==NavalExecutionOperation.Cancel)return x.Intents.Any(p=>p.Operation==NavalExecutionOperation.Submit&&p.ReservationId==r.Id&&p.ClientId==i.ClientId&&p.DispatchRevision!=null&&(i.OrderId==null||p.Outcome?.OrderId==i.OrderId));
        if(i.Operation==NavalExecutionOperation.CancelProtection)return x.ExitRequested.Contains(r.Id)&&x.Protection.Any(p=>p.Instrument==i.Instrument&&p.AlgoId==i.OrderId&&p.AttachmentClientId==i.ClientId&&
            x.IdAllocations.SelectMany(a=>a.Ids).Any(a=>a.ReservationId==r.Id&&a.Role==NavalClientIdRole.ProtectionAlgo&&a.ClientId==p.AttachmentClientId));
        if(i.Operation!=NavalExecutionOperation.Submit)return false;
        using var doc=JsonDocument.Parse(i.Body);var p=doc.RootElement;
        decimal N(JsonElement o,string key)=>decimal.Parse(o.GetProperty(key).GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        if(p.GetProperty("ordType").GetString()!="fok"||p.GetProperty("tdMode").GetString()!="cash")return false;
        if(i.Reduction)
        {
            var spec=_definition.Catalog.Single(z=>z.Instrument==i.Instrument);decimal max=decimal.Floor(r.Inventory/(_definition.Fees.ExitInBase?1+_definition.Fees.ExitRate:1)/spec.Quantum)*spec.Quantum;
            return x.ExitRequested.Contains(r.Id)&&r.TerminalCoverage&&!r.ReductionUnsettled&&r.Inventory>0&&p.GetProperty("side").GetString()=="sell"&&N(p,"sz")>0&&N(p,"sz")<=max&&N(p,"sz")%spec.Quantum==0&&
                !p.TryGetProperty("attachAlgoOrds",out _)&&x.Obligations.All(o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal))&&
                x.Protection.Where(z=>z.Instrument==i.Instrument).GroupBy(z=>z.AlgoId).All(g=>g.Last().State=="canceled")&&
                !x.Intents.Any(z=>z.Id!=i.Id&&z.ReservationId==r.Id&&z.Reduction&&z.Stage!=NavalExecutionStage.NotSent)&&
                x.Orders.Where(z=>z.Instrument==i.Instrument).GroupBy(z=>z.OrderId).All(g=>g.Last().State is "filled" or "canceled");
        }
        if(x.ExitRequested.Any(id=>e.Naval!.Reservations.Any(z=>z.Id==id&&NavalCashRiskPolicy.Active(z))))return false;
        if(x.Intents.Any(z=>z.Id!=i.Id&&z.ReservationId==r.Id&&z.Operation==NavalExecutionOperation.Submit&&!z.Reduction&&z.Stage!=NavalExecutionStage.NotSent))return false;
        return _brokerAuthority?.ProtectionProfile!=null&&p.GetProperty("side").GetString()=="buy"&&N(p,"sz")==r.Quantity&&N(p,"px")==r.Intent.EntryPriceCeiling&&
            _brokerQuotes.TryGetValue(i.Instrument,out var q)&&q.Ask<=r.Intent.EntryPriceCeiling&&q.AskQuantity>=r.Quantity&&p.TryGetProperty("attachAlgoOrds",out var a)&&a.GetArrayLength()==1&&
            N(a[0],"slTriggerPx")==r.Intent.StopPrice&&N(a[0],"tpTriggerPx")==r.Intent.TakeProfitPrice&&x.IdAllocations.SelectMany(z=>z.Ids).Any(z=>z.ReservationId==r.Id&&z.Role==NavalClientIdRole.ProtectionAlgo&&z.ClientId==a[0].GetProperty("attachAlgoClOrdId").GetString());
    }
}
