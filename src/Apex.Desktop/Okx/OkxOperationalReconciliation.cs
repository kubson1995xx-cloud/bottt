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
    private async Task<NavalRiskResult> CollectEconomicsAsync(CancellationToken ct,OkxOrderDiagnostic? diagnostic=null)
    {
        var reads=account.ObservationClient!;var context=account.ObservationContext!;var state=session.ReadState();var b=state.NavalBroker!;var x=b.Operational??new();
        var capture=clock.GetUtcNow();long mono=clock.GetTimestamp();long version=account.ActivityVersion;var activity=account.ReadActivity();var evidence=new List<NavalBrokerSurface>();var problems=new List<string>();
        foreach(var item in activity)
        {
            if(item.MessageDigest!=null)evidence.Add(new("WS:"+item.Channel,"",item.MessageDigest,item.Orders.Length+item.Algos.Length,item.ReceiveUtc,item.ReceiveUtc,item.ReceiveMonotonic,item.ReceiveMonotonic));

        }
        // Caller cancellation is not a new account observation. Keep real read failures
        // distinct so a later STOP cannot erase an already observed failure.
        bool failedRead=false;
        void Failed(string? error){if(!ct.IsCancellationRequested||error is not ("Canceled" or "CanceledOrTimeout" or "ExpiredOrStopped"))failedRead=true;}
        var fills=new List<OkxExecutionFillDto>();var bills=new List<OkxExecutionBillDto>();var orders=new List<OkxExecutionOrderDto>();var algos=new List<NavalBrokerProtection>();
        var origins=new Dictionary<OkxExecutionOrderDto,OkxOrderObservationOrigin>(ReferenceEqualityComparer.Instance);
        void AddOrders(IEnumerable<OkxExecutionOrderDto> rows,OkxOrderObservationOrigin origin)
        {foreach(var row in rows){orders.Add(row);origins[row]=origin;}}
        void RetainOrderDigests(IEnumerable<OkxExecutionOrderDto> rows,NavalBrokerSurface source)
        {
            if(!account.OrdinaryDemoPolicy)return;
            foreach(string digest in rows.Select(o=>o.PayloadDigest).Distinct(StringComparer.Ordinal))
                evidence.Add(source with{Endpoint="ORDER_ROW:"+source.Endpoint,Rows=1,Digest=digest});
        }
        var exactAlgos=new Dictionary<(string,string,string),OkxExecutionAlgoDto>();
        var pager=OkxObservationPager.ForOperationalReconciliation(reads);var begin=DateTimeOffset.FromUnixTimeMilliseconds(b.Baseline.Observations.CaptureUtc.ToUnixTimeMilliseconds());
        var end=DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());if(end<=begin)end=begin.AddMilliseconds(1);
        // Original lookups remain even when their cTime precedes this bounded history scan.
        foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null))
        {
            var detail=await reads.ReadOwnedRecoveryOrderAsync(new(i.Instrument,i.Outcome?.OrderId,i.ClientId),context,ct).ConfigureAwait(false);
            if(detail.Status==OkxObservationStatus.Observed)AddOrders(detail.Rows,OkxOrderObservationOrigin.Exact);else {Failed(detail.ErrorCode);problems.Add("ORIGINAL_ORDER_UNKNOWN:"+i.Id);}
            if(detail.ResponseDigest!=null){evidence.Add(Surface(detail));RetainOrderDigests(detail.Rows,Surface(detail));}
        }
        foreach(var surface in new[]{OkxObservationSurface.Pending,OkxObservationSurface.History,OkxObservationSurface.HistoryArchive,OkxObservationSurface.Fills,OkxObservationSurface.FillsArchive,OkxObservationSurface.Bills,OkxObservationSurface.BillsArchive})
        {
            var page=surface==OkxObservationSurface.Pending?new OkxObservationPageQuery(null,null,null,null,null):new(null,null,begin,end,null);
            var collection=await pager.CollectAsync(new(surface,page,context),ct).ConfigureAwait(false);
            if(collection.Status!=OkxObservationCollectionStatus.Collected){Failed(collection.ErrorCode);problems.Add("HISTORY_INCOMPLETE:"+surface);}
            fills.AddRange(collection.Fills);bills.AddRange(collection.Bills);AddOrders(collection.Orders,surface==OkxObservationSurface.Pending?OkxOrderObservationOrigin.Pending:surface==OkxObservationSurface.History?OkxOrderObservationOrigin.Recent:OkxOrderObservationOrigin.Archive);
            // A terminal/unknown row on the pending surface cannot corroborate an
            // exact terminal order, even when its full payload is identical.
            if(account.OrdinaryDemoPolicy&&surface==OkxObservationSurface.Pending)
                foreach(var pending in collection.Orders.Where(o=>o.State is not ("live" or "partially_filled")))problems.Add("PENDING_ORDER_LIFECYCLE_CONFLICT:"+pending.OrdId);
            foreach(var p in collection.Trace)if(p.ResponseDigest!=null)evidence.Add(new(p.Endpoint,p.ExactQuery,p.ResponseDigest,p.RowCount,p.RequestUtc,p.ReceiveUtc,p.RequestMonotonic,p.ReceiveMonotonic));
            // Row digests span this collection window; original page/query records above
            // remain the endpoint evidence. Do not pretend every row came from its head.
            RetainOrderDigests(collection.Orders,new(RecoveryRequest.Endpoint(surface),"","",0,collection.RequestUtc,collection.ReceiveUtc,collection.RequestMonotonic,collection.ReceiveMonotonic));
        }
        var economicQuotes=new Dictionary<(string,string),string>();
        var uniqueOrders=ConsistentOrders(false);
        OkxExecutionOrderDto[] ConsistentOrders(bool diagnose)
        {
            var selected=new List<OkxExecutionOrderDto>();economicQuotes.Clear();
            foreach(var group in orders.GroupBy(o=>(o.InstId,o.OrdId)))
            {
                var first=group.First();
                bool strict=group.Skip(1).All(o=>account.OrdinaryDemoPolicy?OkxOrderConsistency.Equivalent(first,o):first.PayloadDigest==o.PayloadDigest);
                // Always diagnose the raw disagreement, even if contextual own proof
                // below admits a separate economic quote. Late child rows still veto.
                if(diagnose&&diagnostic!=null&&!strict)
                    foreach(var other in group.Skip(1))
                        if(!OkxOrderConsistency.Equivalent(first,other))diagnostic.Add(first,other,origins[first],origins[other]);
                string? actualQuote=!strict&&account.OrdinaryDemoPolicy&&!account.IsFiniteDemo?
                    OkxOwnCashOrderCompatibility.EconomicQuote(b.Baseline,x,group.ToArray(),o=>origins[o],fills):null;
                if(!strict&&actualQuote==null)
                {
                    string reason="ORDER_CHANGED_DURING_PASS:"+group.Key.OrdId;
                    if(!problems.Contains(reason))problems.Add(reason);

                    continue; // A conflicting endpoint must not win or authorize child ancestry.
                }
                selected.Add(first);if(actualQuote!=null)economicQuotes[group.Key]=actualQuote;
            }
            return selected.ToArray();
        }
        foreach(var o in uniqueOrders)
        {
            var parent=x.Intents.SingleOrDefault(i=>i.Operation==NavalExecutionOperation.Submit&&i.Instrument==o.InstId&&i.ClientId==o.ClOrdId&&i.DispatchRevision!=null&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==o.OrdId));
            if(parent==null)continue; // Classify only after the parent's observed algo→child links are resolved.
            if(!parent.Reduction&&o.AccFillSz.Value>0&&o.AttachAlgoOrds is not {Length: >0})problems.Add("PROTECTION_UNKNOWN:"+parent.Id);
            if(o.AttachAlgoOrds is {} attachments)
            foreach(var a in attachments)
            {
                if(!x.IdAllocations.SelectMany(a=>a.Ids).Any(id=>id.ReservationId==parent.ReservationId&&id.Role==NavalClientIdRole.ProtectionAlgo&&id.ClientId==a.AttachAlgoClOrdId))
                {problems.Add("UNOWNED_ATTACHMENT:"+o.PayloadDigest);continue;}
                if(a.FailCode is not (null or "" or "0")&&string.IsNullOrEmpty(a.AttachAlgoId))
                {
                    // Explicit failed placement with exact original attachment and NO generated
                    // algo. This is unsafe inventory, not active protection or an invented algo ID.
                    algos.Add(new(o.InstId,o.OrdId,a.AttachAlgoClOrdId!,"","order_failed",null,null,null,Array.Empty<string>(),o.PayloadDigest));continue;
                }
                if(a.FailCode is not (null or "" or "0")){problems.Add("PROTECTION_PLACEMENT_CONFLICT:"+parent.Id);continue;}
                if(string.IsNullOrEmpty(a.AttachAlgoId)){problems.Add("PROTECTION_UNKNOWN:"+parent.Id);continue;}
                // attachAlgoId is NOT forwarded to generated algoId. Only the
                // exact locally allocated attachment client ID is forwarded.
                var detail=await reads.ReadOwnedRecoveryAlgoAsync(new(null,a.AttachAlgoClOrdId),context,ct).ConfigureAwait(false);
                if(detail.Status!=OkxObservationStatus.Observed||detail.Rows.Count!=1){Failed(detail.ErrorCode);problems.Add("PROTECTION_UNKNOWN:"+parent.Id);continue;}
                evidence.Add(Surface(detail));
                foreach(var p in detail.Rows)
                {
                    if(p.InstId!=o.InstId||string.IsNullOrEmpty(p.AlgoId)||p.AlgoClOrdId!=a.AttachAlgoClOrdId||p.Side!="sell"||p.TdMode!="cash"||p.TradeQuoteCcy!=b.Baseline.Binding.Account.QuoteCurrency||
                        x.Protection.Any(old=>old.AttachmentClientId==a.AttachAlgoClOrdId&&old.AttachmentId!=null&&(old.Instrument!=p.InstId||old.ParentOrderId!=o.OrdId||old.AttachmentId!=a.AttachAlgoId||old.AlgoId!=p.AlgoId))||
                        p.FailCode is not (null or "" or "0")&&!(p.State=="order_failed"&&p.OrdIdList is not {Length: >0})||
                        Conditional&&p.State=="live"&&(p.OrdType!="oco"||p.TpOrdPx.Value!=-1||p.SlOrdPx.Value!=-1||p.TpTriggerPxType!="last"||p.SlTriggerPxType!="last"))
                    {problems.Add("PROTECTION_IDENTITY_CONFLICT:"+p.PayloadDigest);continue;}
                    if(!p.OrdIdList.HasValue){problems.Add("PROTECTION_CHILD_RELATION_UNPROVEN:"+parent.Id);continue;}
                    exactAlgos[(p.InstId,p.AlgoId,p.AlgoClOrdId)]=p;
                    algos.Add(new(p.InstId,o.OrdId,a.AttachAlgoClOrdId!,p.AlgoId,p.State,p.Sz.Value,p.SlTriggerPx.Value,p.TpTriggerPx.Value,p.OrdIdList.Value.ToArray(),p.PayloadDigest){AttachmentId=a.AttachAlgoId});
                }
            }
        }
        // Resolve each observed protective child by its exact order ID, not cumulative algo fields.
        foreach(var link in algos.ToArray())foreach(string childId in link.ChildOrderIds)
        {
            var child=await reads.ReadOrderAsync(new(link.Instrument,childId,null),context,ct).ConfigureAwait(false);
            bool valid=child.Status==OkxObservationStatus.Observed&&child.Rows.Count==1&&child.Rows.All(o=>o.InstId==link.Instrument&&o.OrdId==childId&&o.Side=="sell"&&o.TdMode=="cash"&&
                (string.IsNullOrEmpty(o.AlgoId)||o.AlgoId==link.AlgoId)&&(string.IsNullOrEmpty(o.AlgoClOrdId)||o.AlgoClOrdId==link.AttachmentClientId));
            if(!valid)
            {
                Failed(child.ErrorCode);
                problems.Add((child.Status==OkxObservationStatus.Observed?"PROTECTIVE_CHILD_IDENTITY_CONFLICT:":"PROTECTIVE_CHILD_UNKNOWN:")+childId);
                // Do not let the rejected child link authorize individual fills in
                // Core merely because the same order also appeared in history.
                algos.Remove(link);
            }
            else AddOrders(child.Rows,OkxOrderObservationOrigin.Child);
            if(child.ResponseDigest!=null){evidence.Add(Surface(child));RetainOrderDigests(child.Rows,Surface(child));}
        }
        uniqueOrders=ConsistentOrders(true);
        // Diagnostic consumes the exact ordinary groups, including late child reads.
        // Stop before any financial commit, acknowledgement, quote or funding update.
        if(diagnostic!=null)
        {
            if(problems.Any(p=>!p.StartsWith("ORDER_CHANGED_DURING_PASS:",StringComparison.Ordinal)))diagnostic.Code="DIAGNOSE_INCOMPLETE";
            return Refuse(diagnostic.Code);
        }
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
        await CollectPendingAlgosAsync(reads,context,(filter,pending)=>
        {
            if(pending.Status!=OkxObservationStatus.Observed){Failed(pending.ErrorCode);problems.Add("PENDING_ALGOS_INCOMPLETE");}
            else foreach(var p in pending.Rows)
            {
                if(!exactAlgos.TryGetValue((p.InstId,p.AlgoId,p.AlgoClOrdId),out var exact)){problems.Add("FOREIGN_OR_UNLINKED_ALGO:"+p.PayloadDigest);continue;}
                problems.AddRange(OkxPendingProtectionProof.Problems(exact,p).Select(reason=>reason+":"+p.PayloadDigest));
            }
            if(pending.Rows.Count>=100)problems.Add("PENDING_ALGO_CAPACITY");if(pending.ResponseDigest!=null)evidence.Add(Surface(pending));
        },ct).ConfigureAwait(false);
        var balance=await reads.ReadBalancesAsync(context,ct).ConfigureAwait(false);var balances=new List<NavalBrokerBalance>();
        if(balance.Status!=OkxObservationStatus.Observed){Failed(balance.ErrorCode);problems.Add("BALANCE_UNKNOWN");}
        else foreach(var d in balance.Rows.Single().Details)balances.Add(new(d.Currency,d.Cash.Value,d.Available.Value,d.Equity.Value,d.Frozen.Value,d.OrderFrozen.Value,d.Liability.Value,d.Interest.Value,d.CrossLiability.Value,d.IsolatedLiability.Value,d.StrategyEquity.Value,d.FixedBalance.Value,d.UTime.Utc));
        if(balance.ResponseDigest!=null)evidence.Add(Surface(balance));
        CurrentMarketFundingEvidenceV1? funding=null;
        if(account.OrdinaryDemoPolicy||b.Baseline.Observations.CurrentMarketFunding!=null)
        {
            try
            {
                if(account.OrdinaryDemoPolicy)funding=await OkxCurrentMarketFunding.ReadOrdinaryAsync(reads,context,account.FundingFingerprint,balances,clock,ct).ConfigureAwait(false);
                else
                {
                    if(account.FundingPremise==null)throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_REQUIRED");
                    funding=await OkxCurrentMarketFunding.ReadAsync(reads,context,account.FundingPremise,account.FundingFingerprint,balances,clock,ct).ConfigureAwait(false);
                }
                evidence.Add(funding.Response);
            }
            catch(Exception ex) when(ex is InvalidOperationException or OperationCanceledException){if(ex is not OperationCanceledException||!ct.IsCancellationRequested)failedRead=true;problems.Add("CURRENT_MARKET_FUNDING_UNPROVEN");}
        }
        // A public-feed outage must not discard already observed cancellation/fills. Retain
        // original source times, publish unsafe coverage, and never rejuvenate an old quote.
        foreach(var i in NavalOrdinaryCatalogPolicy.Catalog(b))
        {
            try{quotes[i.Instrument]=await feed.ReadQuoteAsync(i,ct).ConfigureAwait(false);}
            catch(Exception error) when(error is System.IO.IOException or System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException)
            {if(error is not OperationCanceledException||!ct.IsCancellationRequested)failedRead=true;problems.Add("QUOTE_UNAVAILABLE:"+i.Instrument);}
        }
        var raw=fills.GroupBy(f=>(f.InstId,f.TradeId,f.PayloadDigest)).Select(g=>g.First()).Select(f=>new NavalBrokerRawFill(f.InstId,f.OrdId,f.ClOrdId,f.TradeId,f.BillId,f.Side??"",f.FillSz.Value,f.FillPx.Value,f.Fee.Value,f.FeeCcy,f.TradeQuoteCcy,f.FillTime.Utc,f.PayloadDigest)).ToArray();
        var billRows=bills.GroupBy(v=>(v.BillId,v.PayloadDigest)).Select(g=>g.First()).Select(v=>new NavalBrokerRawBill(v.BillId,v.InstId,v.OrdId,v.ClOrdId,v.TradeId,v.Type,v.Ccy,v.Fee.Value,v.BalChg.Value,v.Ts.Utc,v.PayloadDigest)).ToArray();
        var proofs=uniqueOrders.Select(o=>
        {
            var a=o.AttachAlgoOrds?.FirstOrDefault();var generated=algos.SingleOrDefault(p=>p.Instrument==o.InstId&&p.ParentOrderId==o.OrdId&&p.AttachmentClientId==a?.AttachAlgoClOrdId&&p.AttachmentId==a?.AttachAlgoId);
            return new NavalBrokerOrderProof(o.InstId,o.OrdId,o.ClOrdId,o.State,o.Sz.Value,o.AccFillSz.Value,o.Fee.Value,o.FeeCcy,economicQuotes.GetValueOrDefault((o.InstId,o.OrdId),o.TradeQuoteCcy),a?.AttachAlgoClOrdId,generated?.AlgoId,o.PayloadDigest){AttachmentId=a?.AttachAlgoId};
        }).ToArray();
        if(account.ActivityVersion!=version)problems.Add("STREAM_CHANGED_DURING_RECONCILIATION");
        // Only an interrupted replay of already retained observations may leave the
        // last authoritative economics untouched. New fills/bills/order/protection
        // variants, balance changes, stream activity or real failures keep the strict
        // partial-pass path below (including holds, conflicts and coverage obligations).
        if(ct.IsCancellationRequested)
        {
        bool knownOrders=orders.All(o=>x.Orders.Any(old=>old.Digest==o.PayloadDigest)||x.Evidence.Any(old=>old.Endpoint.StartsWith("ORDER_ROW:",StringComparison.Ordinal)&&old.Digest==o.PayloadDigest));
        bool knownBalances=balances.All(v=>x.LastBalances?.Any(old=>(v with{SourceUtc=old.SourceUtc})==old)==true);
        bool interruptedProblems=problems.All(p=>p.StartsWith("ORIGINAL_ORDER_UNKNOWN:",StringComparison.Ordinal)||p.StartsWith("HISTORY_INCOMPLETE:",StringComparison.Ordinal)||
            p.StartsWith("PROTECTION_UNKNOWN:",StringComparison.Ordinal)||p.StartsWith("QUOTE_UNAVAILABLE:",StringComparison.Ordinal)||
            p is "PENDING_ALGOS_INCOMPLETE" or "BALANCE_UNKNOWN" or "CURRENT_MARKET_FUNDING_UNPROVEN"||
            // Incomplete fills can no longer corroborate an already accepted own-cash
            // metadata pair. Only variants present together in the last pass qualify.
            p.StartsWith("ORDER_CHANGED_DURING_PASS:",StringComparison.Ordinal)&&orders.All(o=>x.Evidence.Any(old=>old.Endpoint.StartsWith("ORDER_ROW:",StringComparison.Ordinal)&&old.Digest==o.PayloadDigest)));
        if(ct.IsCancellationRequested&&!failedRead&&interruptedProblems&&account.ActivityVersion==version&&
            raw.All(x.FillEvidence.Contains)&&billRows.All(x.Bills.Contains)&&knownOrders&&knownBalances&&
            algos.All(p=>x.Protection.Any(old=>old.Digest==p.Digest))&&
            activity.All(a=>a.MessageDigest!=null&&x.Evidence.Any(old=>old.Endpoint.StartsWith("WS:",StringComparison.Ordinal)&&old.Digest==a.MessageDigest)))
            return session.Coordinator.Stop("BROKER_RECONCILIATION_CANCELED");
        }
        // Match the existing durable evidence bound; never truncate provenance or
        // let its overflow turn into a failed financial-state commit.
        if(evidence.Count>1024)return session.Coordinator.Stop("BROKER_EVIDENCE_CAPACITY");
        var result=session.ApplyEconomics(new(capture,mono,problems.Count==0,raw,billRows,proofs,algos,balances,quotes,evidence,problems){CurrentMarketFunding=funding,OperatorPremise=funding==null?null:account.FundingPremise,ObservationCapture=context.CaptureId});
        if(result.Code!="STORAGE_FROZEN"&&result.Code!="COMMIT_CONFLICT"&&activity.Length>0)account.AcknowledgeActivity(activity.Max(p=>p.Sequence));
        if(result.Code is not ("STORAGE_FROZEN" or "COMMIT_CONFLICT")&&account.ActivityVersion==version)Interlocked.Exchange(ref reconciledActivityVersion,version);
        return result;
    }
    private static NavalBrokerSurface Surface<T>(OkxObservationRead<T> r)=>new(r.Endpoint,r.ExactQuery,r.ResponseDigest!,r.Rows.Count,r.RequestUtc,r.ReceiveUtc,r.RequestMonotonic,r.ReceiveMonotonic);
}
