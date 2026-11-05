using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Local ordinary-DEMO evidence sufficiency rule, not a provider normalization.
// Returns a separate economic projection; neither DTO nor retained digest changes.
// Core still requires closed paired bills, exact balances, funding and protection
// before any settlement/PREPARE/START. This helper grants no financial authority.
internal static class OkxOwnCashOrderCompatibility
{
    internal static string? EconomicQuote(NavalBrokerBaseline baseline,NavalBrokerOperationalState state,
        IReadOnlyList<OkxExecutionOrderDto> rows,Func<OkxExecutionOrderDto,OkxOrderObservationOrigin> origin,
        IReadOnlyList<OkxExecutionFillDto> fills)
    {
        if(baseline.Binding.Account.Environment!=NavalEnvironment.OkxDemo||rows.Count<2)return null;
        var first=rows[0];string quote=baseline.Binding.Account.QuoteCurrency;
        var spec=(state.OrdinaryCatalog?.Instruments??baseline.Observations.Catalog).SingleOrDefault(s=>s.Instrument==first.InstId&&s.QuoteCurrency==quote);
        var config=baseline.Observations.Account;
        if(spec==null||config.Level!="1"||config.AutoLoan||config.Borrow||config.AutoRepay||config.Type!="0"||config.SpotRole!="0"||
            !rows.Any(o=>origin(o)==OkxOrderObservationOrigin.Exact)||
            !rows.Any(o=>origin(o) is OkxOrderObservationOrigin.Recent or OkxOrderObservationOrigin.Archive&&o.TradeQuoteCcy==quote)||
            rows.Any(o=>origin(o) is OkxOrderObservationOrigin.Pending or OkxOrderObservationOrigin.Child||
                o.State!="filled"||o.Sz.Value is not >0||o.AccFillSz.Value!=o.Sz.Value||o.Fee.Value is not <=0||
                o.ConsistencyExtras==null||o.ConsistencyExtras.TryGetValue("quickMgnType",out var m)&&m!=""||
                o.ConsistencyExtras.TryGetValue("lever",out var l)&&l is not ("" or "1")||
                !OkxOrderConsistency.EquivalentOwnCash(first,o,quote)))return null;
        var owners=state.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null&&
            i.Stage is NavalExecutionStage.Dispatching or NavalExecutionStage.SubmitUnknown or NavalExecutionStage.Accepted&&
            i.Instrument==first.InstId&&i.ClientId==first.ClOrdId&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==first.OrdId)).ToArray();
        if(owners.Length!=1)return null;
        var own=owners[0];
        try
        {
            using var request=JsonDocument.Parse(own.Body);var r=request.RootElement;
            string? Text(string name)=>r.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
            if(r.ValueKind!=JsonValueKind.Object||r.EnumerateObject().GroupBy(p=>p.Name,StringComparer.Ordinal).Any(g=>g.Count()!=1)||
                Text("tradeQuoteCcy")!=quote||Text("instId")!=spec.Instrument||Text("clOrdId")!=first.ClOrdId||Text("tdMode")!="cash"||
                Text("side")!=(own.Reduction?"sell":"buy")||Text("side")!=first.Side||Text("ordType")!=first.OrdType||
                !decimal.TryParse(Text("sz"),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var quantity)||quantity!=first.Sz.Value)return null;
            var trades=fills.Where(f=>f.InstId==first.InstId&&f.OrdId==first.OrdId)
                .GroupBy(f=>(f.TradeId,f.PayloadDigest)).Select(g=>g.First()).ToArray();
            if(trades.Length==0||trades.GroupBy(f=>f.TradeId).Any(g=>g.Count()!=1)||
                trades.Any(f=>f.InstType!="SPOT"||f.ClOrdId!=own.ClientId||f.Side!=first.Side||string.IsNullOrEmpty(f.TradeId)||string.IsNullOrEmpty(f.BillId)||
                    f.TradeQuoteCcy!=quote||f.FillSz.Value is not >0||f.FillPx.Value is not >0||f.Fee.Value is not <=0||
                    f.FeeCcy!=first.FeeCcy||f.FeeCcy!=spec.BaseCurrency&&f.FeeCcy!=quote||f.FillTime.Utc==null)||
                trades.Sum(f=>f.FillSz.Value)!=first.AccFillSz.Value||trades.Sum(f=>f.Fee.Value)!=first.Fee.Value)return null;
            // Retained individual corrections cannot be hidden by a fresh good page.
            if(state.FillEvidence.Any(old=>old.Instrument==first.InstId&&trades.Any(f=>f.TradeId==old.TradeId&&
                (old.OrderId!=f.OrdId||old.ClientId!=f.ClOrdId||old.Side!=f.Side||old.ActualQuote!=f.TradeQuoteCcy||
                 old.Quantity!=f.FillSz.Value||old.Price!=f.FillPx.Value||old.SignedFee!=f.Fee.Value||old.FeeCurrency!=f.FeeCcy))))return null;
            return quote;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or OverflowException){return null;}
    }
}
