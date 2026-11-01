using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Maps original adapter observations only. A nonempty clean-start activity surface is a blocker,
// not an invitation to truncate history or import foreign capital.
internal static class OkxBrokerObservationMapper
{
    internal static async Task VerifyUnchangedAsync(OkxExecutionObservationClient client,OkxObservationContext context,NavalBrokerObservationBatch original,CancellationToken ct)
    {
        static IReadOnlyList<T> Rows<T>(OkxObservationRead<T> r)
        {if(r.Status!=OkxObservationStatus.Observed)throw new InvalidOperationException("BASELINE_RECHECK_FAILED");return r.Rows;}
        var a=Rows(await client.ReadAccountAsync(context,ct)).Single();
        var account=new NavalBrokerAccountObservation(a.Uid,a.MainUid,a.AccountLevel,a.Permissions,a.Ip,a.FeeType,a.AutoLoan,a.EnableSpotBorrow,a.SpotBorrowAutoRepay,a.AccountType,a.SpotRoleType);
        var balances=Rows(await client.ReadBalancesAsync(context,ct)).Single().Details.Select(d=>new NavalBrokerBalance(d.Currency,d.Cash.Value,d.Available.Value,d.Equity.Value,d.Frozen.Value,d.OrderFrozen.Value,d.Liability.Value,d.Interest.Value,d.CrossLiability.Value,d.IsolatedLiability.Value,d.StrategyEquity.Value,d.FixedBalance.Value,d.UTime.Utc)).ToArray();
        var instruments=Rows(await client.ReadInstrumentsAsync(context,ct));
        if(account!=original.Account||!balances.SequenceEqual(original.Balances))throw new InvalidOperationException("BASELINE_CHANGED");
        foreach(var known in original.Catalog)
        {
            var i=instruments.SingleOrDefault(i=>i.Instrument==known.Instrument)??throw new InvalidOperationException("BASELINE_CATALOG_CHANGED");
            if(known==null||i.InstrumentType!="SPOT"||i.State!="live"||i.CatalogQuote!=known.QuoteCurrency||i.BaseCurrency!=known.BaseCurrency||
                !i.TradeQuoteCurrencies.Contains(known.QuoteCurrency)||i.LotSize.Value!=known.Quantum||i.MinimumSize.Value!=known.MinimumQuantity||i.TickSize.Value!=known.TickSize||i.GroupId==null)throw new InvalidOperationException("BASELINE_CATALOG_CHANGED");
            var f=Rows(await client.ReadFeesAsync(i.Instrument,context,ct)).Single();
            if(!f.Groups.TryGetValue(i.GroupId,out var g)||g.Taker.Value is not <0||g.Maker.Value is not <=0||-g.Taker.Value!=original.Fees.EntryRate)throw new InvalidOperationException("BASELINE_FEES_CHANGED");
        }
    }
    internal static async Task<NavalBrokerObservationBatch> CollectAsync(OkxExecutionObservationClient client,OkxObservationContext context,OkxScopeBinding binding,TimeProvider clock,Func<OkxStreamSnapshot> stream,CancellationToken ct)
    {
        var surfaces=new List<NavalBrokerSurface>();var capture=clock.GetUtcNow();long mono=clock.GetTimestamp();
        IReadOnlyList<T> Observe<T>(OkxObservationRead<T> r,bool empty=false)
        {
            if(r.Status!=OkxObservationStatus.Observed||r.Context!=context||r.RequestedScope.Uid!=binding.Account.AccountId||r.ResponseDigest==null||empty&&r.Rows.Count!=0)throw new InvalidOperationException(empty?"BASELINE_ACTIVITY_PRESENT":"OBSERVATION_FAILED:"+r.ErrorCode);
            surfaces.Add(new(r.Endpoint,r.ExactQuery,r.ResponseDigest,r.Rows.Count,r.RequestUtc,r.ReceiveUtc,r.RequestMonotonic,r.ReceiveMonotonic));return r.Rows;
        }
        var time=Observe(await client.ReadTimeAsync(context,ct)).Single().Ts.Utc??throw new InvalidOperationException("CLOCK_UNPROVEN");
        var account=Observe(await client.ReadAccountAsync(context,ct)).Single();
        var balance=Observe(await client.ReadBalancesAsync(context,ct)).Single();
        var instruments=Observe(await client.ReadInstrumentsAsync(context,ct));
        // One discovered exact quote; no alias, conversion or per-fleet capital.
        var assets=NavalFleetProfiles.Get(NavalFleet.Bravo).Assets;
        var supported=instruments.Where(i=>assets.Contains(i.BaseCurrency,StringComparer.Ordinal)&&i.CatalogQuote==binding.Account.QuoteCurrency&&i.TradeQuoteCurrencies.Contains(binding.Account.QuoteCurrency)&&i.State=="live").OrderBy(i=>i.BaseCurrency=="BTC"?0:1).ThenBy(i=>i.Instrument,StringComparer.Ordinal).ToArray();
        if(supported.Length==0||!supported.Any(i=>i.BaseCurrency=="BTC"))throw new InvalidOperationException("CATALOG_UNSUPPORTED");
        var catalog=new List<NavalInstrument>();NavalFeeSchedule? fees=null;
        foreach(var i in supported)
        {
            if(i.InstrumentType!="SPOT"||i.State!="live"||i.CatalogQuote!=binding.Account.QuoteCurrency||!i.TradeQuoteCurrencies.Contains(binding.Account.QuoteCurrency)||
                i.GroupId==null||i.LotSize.Value is not >0||i.MinimumSize.Value is not >0||i.TickSize.Value is not >0||i.ListTime.Utc>capture||i.ExpiryTime.Utc!=null)throw new InvalidOperationException("CATALOG_UNSUPPORTED");
            var f=Observe(await client.ReadFeesAsync(i.Instrument,context,ct)).Single();
            if(f.InstrumentType!="SPOT"||f.Timestamp.Utc==null||f.Timestamp.Utc>clock.GetUtcNow()||clock.GetUtcNow()-f.Timestamp.Utc>=TimeSpan.FromSeconds(30)||
                !f.Groups.TryGetValue(i.GroupId,out var group)||group.Maker.Value is not <=0||group.Taker.Value is not <0||group.Taker.Value<=-1)throw new InvalidOperationException("FEE_SCOPE_UNSUPPORTED");
            var schedule=new NavalFeeSchedule(-group.Taker.Value.Value,account.FeeType=="0",-group.Taker.Value.Value,false);
            // The immutable baseline supports a named same-schedule subset, not the venue's entire catalog.
            if(fees!=null&&schedule!=fees)continue;fees=schedule;
            catalog.Add(new(i.Instrument,i.BaseCurrency,i.CatalogQuote,i.LotSize.Value.Value,i.MinimumSize.Value.Value,i.TickSize.Value.Value));
        }
        var pending=new OkxObservationPageQuery(null,null,null,null,null);
        Observe(await client.ReadPendingAsync(pending,context,ct),true);
        // Candidate cut is explicitly bounded to the prior day. Exact protected approval nominates
        // this initial boundary; it is not a claim about lifelong or hidden broker history.
        var end=DateTimeOffset.FromUnixTimeMilliseconds(capture.ToUnixTimeMilliseconds());var begin=end.AddDays(-1);
        var page=new OkxObservationPageQuery(null,null,begin,end,null);
        foreach(var window in new[]{OkxObservationWindow.Recent,OkxObservationWindow.Archive})
        {
            Observe(await client.ReadHistoryAsync(window,page,context,ct),true);
            Observe(await client.ReadFillsAsync(window,page,context,ct),true);
            Observe(await client.ReadBillsAsync(window,page,context,ct),true);
        }
        foreach(var filter in new[]{OkxAlgoOrderFilter.ConditionalAndOco,OkxAlgoOrderFilter.Trigger,OkxAlgoOrderFilter.TrailingStop})
        {
            Observe(await client.ReadPendingAlgosAsync(new(OkxAlgoObservationSurface.Pending,filter,null,null,null,null),context,ct),true);
            foreach(var state in new[]{OkxAlgoHistoryState.Effective,OkxAlgoHistoryState.Canceled,OkxAlgoHistoryState.OrderFailed})
                Observe(await client.ReadAlgoHistoryAsync(new(OkxAlgoObservationSurface.History,filter,state,null,null,null),context,ct),true);
        }
        var after=Observe(await client.ReadBalancesAsync(context,ct)).Single();
        if(!balance.Details.SequenceEqual(after.Details))throw new InvalidOperationException("BASELINE_BALANCE_MOVED");
        var afterAccount=Observe(await client.ReadAccountAsync(context,ct)).Single();
        if(account!=afterAccount)throw new InvalidOperationException("BASELINE_IDENTITY_MOVED");
        var stateNow=stream();if(!stateNow.PairedSubscribed||stateNow.Epoch==null||stateNow.HasGap)throw new InvalidOperationException("OBSERVATION_GAP");
        var balances=balance.Details.Select(d=>new NavalBrokerBalance(d.Currency,d.Cash.Value,d.Available.Value,d.Equity.Value,d.Frozen.Value,d.OrderFrozen.Value,d.Liability.Value,d.Interest.Value,d.CrossLiability.Value,d.IsolatedLiability.Value,d.StrategyEquity.Value,d.FixedBalance.Value,d.UTime.Utc)).ToArray();
        var a=new NavalBrokerAccountObservation(account.Uid,account.MainUid,account.AccountLevel,account.Permissions,account.Ip,account.FeeType,account.AutoLoan,account.EnableSpotBorrow,account.SpotBorrowAutoRepay,account.AccountType,account.SpotRoleType);
        return new(context.ConnectionId,context.CaptureId,stateNow.Epoch.Value,stateNow.Sequence,capture,mono,time,a,balances,catalog.ToArray(),fees!,surfaces.ToArray(),NavalJson.Hash(catalog.ToArray()),NavalJson.Hash(fees!));
    }
}
