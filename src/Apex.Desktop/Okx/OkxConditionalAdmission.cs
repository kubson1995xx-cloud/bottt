using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed partial class OkxOperationalTradingService
{
    private readonly Dictionary<string,OkxExecutionInstrumentDto> privateSpecs=new(StringComparer.Ordinal);
    private readonly Dictionary<string,NavalBrokerQuote> limitReferences=new(StringComparer.Ordinal);
    private DateTimeOffset specUtc;
    private long specTimestamp;
    private bool Conditional=>protection==OkxConditionalCashSpotPolicy.Id;
    private bool FreshReference(NavalBrokerQuote q)=>q.Bid>0&&q.Ask>=q.Bid&&q.AskQuantity>0&&q.SourceUtc<=q.ReceivedUtc&&q.ReceivedUtc<=clock.GetUtcNow()&&clock.GetUtcNow()-q.SourceUtc<TimeSpan.FromSeconds(5)&&clock.GetTimestamp()>=q.ReceivedTimestamp;
    private async Task RefreshLimitReferences(NavalInstrument spec,NavalBrokerQuote quote,CancellationToken ct)
    {
        var observed=Observed(await account.ObservationClient!.ReadInstrumentsAsync(account.ObservationContext!,ct).ConfigureAwait(false)).SingleOrDefault(i=>i.Instrument==spec.Instrument);
        if(observed==null||observed.State!="live"||observed.InstrumentType!="SPOT"||observed.BaseCurrency!=spec.BaseCurrency||observed.CatalogQuote!=spec.QuoteCurrency||!observed.TradeQuoteCurrencies.Contains(spec.QuoteCurrency)||observed.TickSize.Value!=spec.TickSize||observed.LotSize.Value!=spec.Quantum||observed.MinimumSize.Value!=spec.MinimumQuantity)throw new InvalidOperationException("BROKER_SPEC_CHANGED");
        privateSpecs[spec.Instrument]=observed;specUtc=clock.GetUtcNow();specTimestamp=clock.GetTimestamp();
        limitReferences[spec.Instrument]=quote;
        foreach(string currency in new[]{"USD","USDT"})
        {
            string instrument=spec.BaseCurrency+"-"+currency;
            limitReferences[instrument]=instrument==spec.Instrument?quote:(account.IsFiniteDemo||OrdinaryDemoPolicy)&&currency=="USD"?await feed.ReadUsdIndexAsync(spec,ct).ConfigureAwait(false):await feed.ReadQuoteAsync(spec with{Instrument=instrument,QuoteCurrency=currency},ct).ConfigureAwait(false);
        }
    }
    // USD and USDT caps are distinct units. Use actual source-timestamped base/reference
    // books, never stablecoin parity or silently treating a USD cap as quote units.
    // This is present-time admission; OKX still enforces its caps when a later algo triggers.
    private bool WithinPrivateMaxima(string instrument,decimal size,decimal price,bool attached)
    {
        if(!Conditional)return true;
        if(!privateSpecs.TryGetValue(instrument,out var spec)||clock.GetUtcNow()<specUtc||clock.GetUtcNow()-specUtc>=TimeSpan.FromSeconds(30)||clock.GetTimestamp()<specTimestamp||clock.GetElapsedTime(specTimestamp)>=TimeSpan.FromSeconds(30)||
            !limitReferences.TryGetValue(instrument,out var actual)||!FreshReference(actual)||size<=0||price<=0)return false;
        bool Cap(string field,string unit,decimal quantity)=>spec.MaxValues.TryGetValue(field,out var max)&&max.Unit==unit&&max.Amount.Value is >0&&quantity<=max.Amount.Value;
        decimal Amount(string currency)
        {
            if(!limitReferences.TryGetValue(spec.BaseCurrency+"-"+currency,out var reference)||!FreshReference(reference))return decimal.MaxValue;
            // Include the requested price ceiling and both requested SL/TP extrema supplied by caller.
            return checked(size*Math.Max(reference.Ask,checked(price/actual.Bid*reference.Ask)));
        }
        try
        {
            if(!Cap("maxLmtSz","base currency",size)||!Cap("maxLmtAmt","USD",Amount("USD")))return false;
            return !attached||(Cap("maxMktSz","USDT",Amount("USDT"))&&Cap("maxMktAmt","USD",Amount("USD"))&&Cap("maxStopSz","USDT",Amount("USDT"))&&Cap("maxTriggerSz","base currency",size));
        }
        catch(OverflowException){return false;}
    }
    private bool PreparedAdmission(OkxPreparedRequest request)
    {
        if(account.IsFiniteDemo&&!FinitePreparedAdmission(request))return false;
        if(!Conditional||request.Operation!=OkxExecutionOperation.Submit)return true;
        using var doc=JsonDocument.Parse(request.CopyBody());var row=doc.RootElement;
        decimal N(string key)=>decimal.Parse(row.GetProperty(key).GetString()!,CultureInfo.InvariantCulture);
        decimal price=N("px");bool attached=row.TryGetProperty("attachAlgoOrds",out var a);
        if(attached)price=Math.Max(price,Math.Max(decimal.Parse(a[0].GetProperty("slTriggerPx").GetString()!,CultureInfo.InvariantCulture),decimal.Parse(a[0].GetProperty("tpTriggerPx").GetString()!,CultureInfo.InvariantCulture)));
        return WithinPrivateMaxima(request.Instrument,N("sz"),price,attached);
    }
}
