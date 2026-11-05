using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed partial class OkxOperationalTradingService
{
    internal async Task<NavalRiskResult> PrepareOrdinaryDemoAsync(CancellationToken ct)
    {
        if(!OrdinaryDemoPolicy||armed||session.Coordinator.Snapshot().Mode!="STOP")return Refuse("ORDINARY_DEMO_UNARMED_STOP_REQUIRED");
        var observed=await ReconcileAsync(ct).ConfigureAwait(false);
        var x=session.ReadState().NavalBroker!.Operational;
        if(observed.Status!=NavalMutationStatus.Applied&&!(x?.Obligations.Count==1&&observed.Code==x.Obligations[0]&&observed.Code.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal)))return Refuse("ORDINARY_DEMO_FRESH_RECONCILIATION_REQUIRED");
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!SourceCurrent)return Refuse("FRESH_CONNECTION_REQUIRED");
            var prepared=session.PrepareOrdinaryDemo();
            if(prepared.Status is not (NavalMutationStatus.Applied or NavalMutationStatus.Duplicate))return prepared;
            var catalog=await AdmitCatalogAsync(ct).ConfigureAwait(false);
            return catalog?.Status is NavalMutationStatus.Refused or NavalMutationStatus.Stopped?catalog:prepared;
        }
        finally{lane.Release();}
    }
    private async Task<NavalRiskResult?> AdmitCatalogAsync(CancellationToken ct)
    {
        var b=session.ReadState().NavalBroker!;
        if(b.Operational?.OrdinaryCatalog!=null||b.Baseline.Binding.Account.QuoteCurrency!="EUR")return null;
        var at=clock.GetUtcNow();long timestamp=clock.GetTimestamp();var reads=account.ObservationClient!;var context=account.ObservationContext!;
        var observation=await reads.ReadInstrumentsAsync(context,ct).ConfigureAwait(false);var rows=Observed(observation);
        var catalog=new List<NavalInstrument>(b.Baseline.Observations.Catalog);var digests=new List<string>{observation.ResponseDigest!};
        foreach(string asset in new[]{"BTC","ETH"})
        {
            var i=rows.SingleOrDefault(i=>i.Instrument==asset+"-EUR");
            if(i==null||i.BaseCurrency!=asset||i.InstrumentType!="SPOT"||i.State!="live"||i.CatalogQuote!="EUR"||!i.TradeQuoteCurrencies.Contains("EUR")||
                i.LotSize.Value is not >0||i.MinimumSize.Value is not >0||i.TickSize.Value is not >0||i.ListTime.Utc==null||i.ListTime.Utc>clock.GetUtcNow()||i.ExpiryTime.Utc!=null)continue;
            var feeRead=await reads.ReadFeesAsync(i.Instrument,context,ct).ConfigureAwait(false);var fee=Observed(feeRead).Single();
            if(i.GroupId==null||!fee.Groups.TryGetValue(i.GroupId,out var group)||group.Taker.Value!=-b.Baseline.Observations.Fees.EntryRate||group.Maker.Value is not <=0||
                fee.Timestamp.Utc==null||fee.Timestamp.Utc>clock.GetUtcNow()||clock.GetUtcNow()-fee.Timestamp.Utc>=TimeSpan.FromSeconds(30))continue;
            catalog.Add(new(i.Instrument,i.BaseCurrency,i.CatalogQuote,i.LotSize.Value.Value,i.MinimumSize.Value.Value,i.TickSize.Value.Value));digests.Add(feeRead.ResponseDigest!);
        }
        // Do not seal an empty expansion: later fresh PREPARE may discover availability.
        return catalog.Count==b.Baseline.Observations.Catalog.Count?null:session.AdmitOrdinaryCatalog(catalog,at,timestamp,NavalJson.Hash(digests));
    }
    // Read-only D1 regime reference. It never joins the financial/trading catalog,
    // seeds BTC capital, or grants a BTC mutation/holding assumption.
    private async Task<NavalInstrument?> BenchmarkAsync(NavalInstrument trading,CancellationToken ct)
    {
        var rows=Observed(await account.ObservationClient!.ReadInstrumentsAsync(account.ObservationContext!,ct).ConfigureAwait(false));
        var i=rows.SingleOrDefault(i=>i.Instrument=="BTC-"+trading.QuoteCurrency&&i.BaseCurrency=="BTC");
        if(i==null||i.InstrumentType!="SPOT"||i.State!="live"||i.CatalogQuote!=trading.QuoteCurrency||!i.TradeQuoteCurrencies.Contains(trading.QuoteCurrency)||i.LotSize.Value is not >0||i.MinimumSize.Value is not >0||i.TickSize.Value is not >0||i.ListTime.Utc==null||i.ListTime.Utc>clock.GetUtcNow()||i.ExpiryTime.Utc!=null)
            return null;
        return new(i.Instrument,i.BaseCurrency,i.CatalogQuote,i.LotSize.Value.Value,i.MinimumSize.Value.Value,i.TickSize.Value.Value);
    }
}
