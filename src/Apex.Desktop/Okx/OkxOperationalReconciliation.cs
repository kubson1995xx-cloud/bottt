using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed partial class OkxOperationalTradingService
{
    private async Task<NavalRiskResult> CollectEconomicsAsync(CancellationToken ct)
    {
        var reads=account.ObservationClient!;var context=account.ObservationContext!;var state=session.ReadState();var b=state.NavalBroker!;var x=b.Operational??new();
        var capture=clock.GetUtcNow();long mono=clock.GetTimestamp();long version=account.ActivityVersion;var activity=account.ReadActivity();var evidence=new List<NavalBrokerSurface>();var problems=new List<string>();
        foreach(var item in activity)
        {
            if(item.MessageDigest!=null)evidence.Add(new("WS:"+item.Channel,"",item.MessageDigest,item.Orders.Length+item.Algos.Length,item.ReceiveUtc,item.ReceiveUtc,item.ReceiveMonotonic,item.ReceiveMonotonic));

        }
        var fills=new List<OkxExecutionFillDto>();var bills=new List<OkxExecutionBillDto>();var orders=new List<OkxExecutionOrderDto>();var algos=new List<NavalBrokerProtection>();
        var pager=new OkxObservationPager(reads);var begin=DateTimeOffset.FromUnixTimeMilliseconds(b.Baseline.Observations.CaptureUtc.ToUnixTimeMilliseconds());
        var end=DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());if(end<=begin)end=begin.AddMilliseconds(1);
        // Original lookups remain even when their cTime precedes this bounded history scan.
        foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null))
        {
            var detail=await reads.ReadOrderAsync(new(i.Instrument,i.Outcome?.OrderId,i.ClientId),context,ct).ConfigureAwait(false);
            if(detail.Status==OkxObservationStatus.Observed)orders.AddRange(detail.Rows);else problems.Add("ORIGINAL_ORDER_UNKNOWN:"+i.Id);
            if(detail.ResponseDigest!=null)evidence.Add(Surface(detail));
        }
        foreach(var surface in new[]{OkxObservationSurface.Pending,OkxObservationSurface.History,OkxObservationSurface.HistoryArchive,OkxObservationSurface.Fills,OkxObservationSurface.FillsArchive,OkxObservationSurface.Bills,OkxObservationSurface.BillsArchive})
        {
            var page=surface==OkxObservationSurface.Pending?new OkxObservationPageQuery(null,null,null,null,null):new(null,null,begin,end,null);
            var collection=await pager.CollectAsync(new(surface,page,context),ct).ConfigureAwait(false);
            if(collection.Status!=OkxObservationCollectionStatus.Collected)problems.Add("HISTORY_INCOMPLETE:"+surface);
            fills.AddRange(collection.Fills);bills.AddRange(collection.Bills);orders.AddRange(collection.Orders);
            foreach(var p in collection.Trace)if(p.ResponseDigest!=null)evidence.Add(new(p.Endpoint,p.ExactQuery,p.ResponseDigest,p.RowCount,p.RequestUtc,p.ReceiveUtc,p.RequestMonotonic,p.ReceiveMonotonic));
        }
        var uniqueOrders=orders.GroupBy(o=>(o.InstId,o.OrdId)).Select(g=>g.Last()).ToArray();
        foreach(var g in orders.GroupBy(o=>(o.InstId,o.OrdId)))if(g.Select(o=>o.PayloadDigest).Distinct().Count()>1)problems.Add("ORDER_CHANGED_DURING_PASS:"+g.Key.OrdId);
        foreach(var o in uniqueOrders)
        {
            var parent=x.Intents.SingleOrDefault(i=>i.Instrument==o.InstId&&i.ClientId==o.ClOrdId&&i.DispatchRevision!=null&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==o.OrdId));
            if(parent==null)continue; // Classify only after the parent's observed algo→child links are resolved.
            if(o.AttachAlgoOrds is {} attachments)
            foreach(var a in attachments.Where(a=>!string.IsNullOrEmpty(a.AttachAlgoId)))
            {
                if(!x.IdAllocations.SelectMany(a=>a.Ids).Any(id=>id.ReservationId==parent.ReservationId&&id.Role==NavalClientIdRole.ProtectionAlgo&&id.ClientId==a.AttachAlgoClOrdId))
                {problems.Add("UNOWNED_ATTACHMENT:"+o.PayloadDigest);continue;}
                var detail=await reads.ReadAlgoAsync(new(a.AttachAlgoId,a.AttachAlgoClOrdId),context,ct).ConfigureAwait(false);
                if(detail.Status!=OkxObservationStatus.Observed){problems.Add("PROTECTION_UNKNOWN:"+parent.Id);continue;}
                evidence.Add(Surface(detail));
                foreach(var p in detail.Rows)
                {
                    if(p.InstId!=o.InstId||p.AlgoId!=a.AttachAlgoId||p.AlgoClOrdId!=a.AttachAlgoClOrdId||p.Side!="sell"||p.TdMode!="cash"||p.TradeQuoteCcy!=b.Baseline.Binding.Account.QuoteCurrency||p.FailCode is not (null or "" or "0"))
                    {problems.Add("PROTECTION_IDENTITY_CONFLICT:"+p.PayloadDigest);continue;}
                    algos.Add(new(p.InstId,o.OrdId,a.AttachAlgoClOrdId!,p.AlgoId,p.State,p.Sz.Value,p.SlTriggerPx.Value,p.TpTriggerPx.Value,p.OrdIdList?.ToArray()??Array.Empty<string>(),p.PayloadDigest));
                }
            }
        }
        // Resolve each observed protective child by its exact order ID, not cumulative algo fields.
        foreach(var link in algos.ToArray())foreach(string childId in link.ChildOrderIds)
        {
            var child=await reads.ReadOrderAsync(new(link.Instrument,childId,null),context,ct).ConfigureAwait(false);
            if(child.Status!=OkxObservationStatus.Observed)problems.Add("PROTECTIVE_CHILD_UNKNOWN:"+childId);
            else {foreach(var o in child.Rows)if(o.InstId==link.Instrument&&o.OrdId==childId&&o.Side=="sell"&&o.TdMode=="cash")orders.Add(o);else problems.Add("PROTECTIVE_CHILD_IDENTITY_CONFLICT:"+childId);}
            if(child.ResponseDigest!=null)evidence.Add(Surface(child));
        }
        uniqueOrders=orders.GroupBy(o=>(o.InstId,o.OrdId)).Select(g=>g.Last()).ToArray();
        bool OwnedOrder(string instrument,string clientId,string orderId)=>x.Intents.Any(i=>i.Operation==NavalExecutionOperation.Submit&&i.Instrument==instrument&&i.ClientId==clientId&&i.DispatchRevision!=null&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==orderId))||
            algos.Concat(x.Protection).Any(p=>p.Instrument==instrument&&p.ChildOrderIds.Contains(orderId));
        foreach(var o in uniqueOrders)
            if(!OwnedOrder(o.InstId,o.ClOrdId,o.OrdId))problems.Add("FOREIGN_ORDER:"+o.PayloadDigest);
        // Stream and REST orders share the same exact ancestry, resolved above before classification.
        foreach(var item in activity)
        {
            if(item.Orders.Any(o=>!OwnedOrder(o.InstId,o.ClOrdId,o.OrdId)))problems.Add("FOREIGN_STREAM_ORDER:"+item.MessageDigest);
            if(item.Algos.Any(a=>!algos.Concat(x.Protection).Any(p=>p.Instrument==a.Algo.InstId&&p.AlgoId==a.Algo.AlgoId&&p.AttachmentClientId==a.Algo.AlgoClOrdId)))problems.Add("FOREIGN_STREAM_ALGO:"+item.MessageDigest);
        }
        // Account-wide pending algo surfaces retain foreign commitments; no truncation of normal catalog.
        foreach(var filter in new[]{OkxAlgoOrderFilter.ConditionalAndOco,OkxAlgoOrderFilter.Trigger,OkxAlgoOrderFilter.TrailingStop})
        {
            var pending=await reads.ReadPendingAlgosAsync(new(OkxAlgoObservationSurface.Pending,filter,null,null,null,null),context,ct).ConfigureAwait(false);
            if(pending.Status!=OkxObservationStatus.Observed)problems.Add("PENDING_ALGOS_INCOMPLETE");
            else foreach(var p in pending.Rows)if(!algos.Any(a=>a.AlgoId==p.AlgoId&&a.Instrument==p.InstId))problems.Add("FOREIGN_OR_UNLINKED_ALGO:"+p.PayloadDigest);
            if(pending.Rows.Count>=100)problems.Add("PENDING_ALGO_CAPACITY");if(pending.ResponseDigest!=null)evidence.Add(Surface(pending));
        }
        var balance=await reads.ReadBalancesAsync(context,ct).ConfigureAwait(false);var balances=new List<NavalBrokerBalance>();
        if(balance.Status!=OkxObservationStatus.Observed)problems.Add("BALANCE_UNKNOWN");
        else foreach(var d in balance.Rows.Single().Details)balances.Add(new(d.Currency,d.Cash.Value,d.Available.Value,d.Equity.Value,d.Frozen.Value,d.OrderFrozen.Value,d.Liability.Value,d.Interest.Value,d.CrossLiability.Value,d.IsolatedLiability.Value,d.StrategyEquity.Value,d.FixedBalance.Value,d.UTime.Utc));
        if(balance.ResponseDigest!=null)evidence.Add(Surface(balance));
        quotes.Clear();foreach(var i in b.Baseline.Observations.Catalog)quotes.Add(i.Instrument,await feed.ReadQuoteAsync(i,ct).ConfigureAwait(false));
        var raw=fills.GroupBy(f=>(f.InstId,f.TradeId,f.PayloadDigest)).Select(g=>g.First()).Select(f=>new NavalBrokerRawFill(f.InstId,f.OrdId,f.ClOrdId,f.TradeId,f.BillId,f.Side??"",f.FillSz.Value,f.FillPx.Value,f.Fee.Value,f.FeeCcy,f.TradeQuoteCcy,f.FillTime.Utc,f.PayloadDigest)).ToArray();
        var billRows=bills.GroupBy(v=>(v.BillId,v.PayloadDigest)).Select(g=>g.First()).Select(v=>new NavalBrokerRawBill(v.BillId,v.InstId,v.OrdId,v.ClOrdId,v.TradeId,v.Type,v.Ccy,v.Fee.Value,v.BalChg.Value,v.Ts.Utc,v.PayloadDigest)).ToArray();
        var proofs=uniqueOrders.Select(o=>
        {
            var a=o.AttachAlgoOrds?.FirstOrDefault();return new NavalBrokerOrderProof(o.InstId,o.OrdId,o.ClOrdId,o.State,o.Sz.Value,o.AccFillSz.Value,o.Fee.Value,o.FeeCcy,o.TradeQuoteCcy,a?.AttachAlgoClOrdId,a?.AttachAlgoId,o.PayloadDigest);
        }).ToArray();
        if(account.ActivityVersion!=version)problems.Add("STREAM_CHANGED_DURING_RECONCILIATION");
        var result=session.ApplyEconomics(new(capture,mono,problems.Count==0,raw,billRows,proofs,algos,balances,quotes,evidence,problems));
        if(result.Code!="STORAGE_FROZEN"&&result.Code!="COMMIT_CONFLICT"&&activity.Length>0)account.AcknowledgeActivity(activity.Max(p=>p.Sequence));
        if(result.Code is not ("STORAGE_FROZEN" or "COMMIT_CONFLICT")&&account.ActivityVersion==version)Interlocked.Exchange(ref reconciledActivityVersion,version);
        return result;
    }
    private static NavalBrokerSurface Surface<T>(OkxObservationRead<T> r)=>new(r.Endpoint,r.ExactQuery,r.ResponseDigest!,r.Rows.Count,r.RequestUtc,r.ReceiveUtc,r.RequestMonotonic,r.ReceiveMonotonic);
}
