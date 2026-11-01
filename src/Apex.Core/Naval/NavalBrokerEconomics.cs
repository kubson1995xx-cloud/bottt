using System;
using System.Collections.Generic;
using System.Linq;
namespace Apex.Core.Naval;

// Original signed fee and observed actual quote retained separately from the once-only posting.
public sealed record NavalBrokerRawFill(string Instrument,string OrderId,string ClientId,string TradeId,string BillId,string Side,
    decimal? Quantity,decimal? Price,decimal? SignedFee,string? FeeCurrency,string? ActualQuote,DateTimeOffset? TradeUtc,string Digest);
public sealed record NavalBrokerRawBill(string Id,string? Instrument,string? OrderId,string? ClientId,string? TradeId,string Type,string Currency,
    decimal? SignedFee,decimal? BalanceChange,DateTimeOffset? SourceUtc,string Digest);
public sealed record NavalBrokerOrderProof(string Instrument,string OrderId,string ClientId,string? State,decimal? Quantity,decimal? Accumulated,
    decimal? SignedFee,string? FeeCurrency,string? ActualQuote,string? AttachmentClientId,string? AlgoId,string Digest);
public sealed record NavalBrokerProtection(string Instrument,string ParentOrderId,string AttachmentClientId,string AlgoId,string? State,
    decimal? Quantity,decimal? Stop,decimal? TakeProfit,IReadOnlyList<string> ChildOrderIds,string Digest);
internal sealed record NavalBrokerEconomicPass(DateTimeOffset CaptureUtc,long CaptureTimestamp,bool CollectionClosed,
    IReadOnlyList<NavalBrokerRawFill> Fills,IReadOnlyList<NavalBrokerRawBill> Bills,IReadOnlyList<NavalBrokerOrderProof> Orders,
    IReadOnlyList<NavalBrokerProtection> Protection,IReadOnlyList<NavalBrokerBalance> Balances,
    IReadOnlyDictionary<string,NavalBrokerQuote> Quotes,IReadOnlyList<NavalBrokerSurface> Evidence,IReadOnlyList<string> Problems);
internal sealed partial class NavalBrokerSession
{
    internal NavalRiskResult ApplyEconomics(NavalBrokerEconomicPass pass)=>Coordinator.ApplyBrokerEconomics(this,pass);
}
public sealed partial class NavalPortfolioCoordinator
{
    internal NavalRiskResult ApplyBrokerEconomics(NavalBrokerSession session,NavalBrokerEconomicPass pass)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational??new();
            if(!BrokerSource(session)||_frozen)return Result(s,NavalMutationStatus.Refused,"BROKER_SOURCE_REQUIRED");
            var obligations=new HashSet<string>(x.Obligations.Where(p=>p.StartsWith("FOREIGN_",StringComparison.Ordinal)||p.StartsWith("UNLINKED_BILL:",StringComparison.Ordinal)||p.StartsWith("FILL_CORRECTION:",StringComparison.Ordinal)).Concat(pass.Problems),StringComparer.Ordinal);
            var knownProtection=x.Protection.Concat(pass.Protection).Select((p,n)=>(p,n)).GroupBy(v=>NavalJson.Hash(v.p)).Select(g=>g.Last()).OrderBy(v=>v.n).Select(v=>v.p).ToArray();
            var raw=x.FillEvidence.Concat(pass.Fills).Distinct().ToArray();var bills=x.Bills.Concat(pass.Bills).Distinct().ToArray();
            if(raw.Length>8192||bills.Length>16384||x.Intents.Count>4096)return StopCandidate(e,"BROKER_EVIDENCE_CAPACITY");
            var candidate=s;bool conflict=false;var settledIntents=x.SettledIntents.ToHashSet(StringComparer.Ordinal);
            foreach(var f in pass.Fills.OrderBy(f=>f.TradeUtc).ThenBy(f=>f.TradeId,StringComparer.Ordinal))
            {
                var i=x.Intents.SingleOrDefault(i=>i.Operation==NavalExecutionOperation.Submit&&i.Instrument==f.Instrument&&i.ClientId==f.ClientId&&i.DispatchRevision!=null&&
                    (i.Outcome?.OrderId==null||i.Outcome.OrderId==f.OrderId));
                bool reduction=i?.Reduction==true;
                if(i==null&&f.Side=="sell")
                {
                    var links=knownProtection.Where(p=>p.Instrument==f.Instrument&&p.ChildOrderIds.Contains(f.OrderId)).ToArray();
                    var owners=x.Intents.Where(p=>p.Operation==NavalExecutionOperation.Submit&&p.Instrument==f.Instrument&&p.DispatchRevision!=null&&links.Any(link=>
                        (p.Outcome?.OrderId==link.ParentOrderId||pass.Orders.Any(o=>o.ClientId==p.ClientId&&o.OrderId==link.ParentOrderId))&&
                        x.IdAllocations.SelectMany(a=>a.Ids).Any(id=>id.ReservationId==p.ReservationId&&id.Role==NavalClientIdRole.ProtectionAlgo&&id.ClientId==link.AttachmentClientId))).ToArray();
                    if(owners.Length==1){i=owners[0];reduction=true;}
                }
                if(i==null){obligations.Add("FOREIGN_FILL:"+f.Digest);continue;}
                if(f.Side!=(reduction?"sell":"buy")||f.ActualQuote!=b.Baseline.Binding.Account.QuoteCurrency||f.Quantity is not >0||f.Price is not >0||f.SignedFee is not <=0||
                    f.FeeCurrency==null||f.TradeUtc==null||f.TradeUtc>pass.CaptureUtc||string.IsNullOrEmpty(f.TradeId))
                {obligations.Add("UNSUPPORTED_FILL_OR_FEE:"+f.Digest);continue;}
                decimal gross;
                try{gross=checked(f.Quantity.Value*f.Price.Value);}catch(OverflowException){obligations.Add("ECONOMIC_OVERFLOW:"+f.Digest);continue;}
                if(gross==0||gross/f.Quantity.Value!=f.Price.Value){obligations.Add("ECONOMIC_PRECISION_LOSS:"+f.Digest);continue;}
                var fill=new NavalFill(b.Baseline.Binding.Account,f.Instrument,f.TradeId,i.ReservationId,reduction,f.Quantity.Value,f.Price.Value,-f.SignedFee.Value,f.FeeCurrency,f.TradeUtc.Value);
                string key=NavalCoreStateOwner.FillKey(fill),digest=NavalJson.Hash(fill);var accepted=candidate.Fills.SingleOrDefault(p=>p.Key==key);
                if(accepted!=null)
                {
                    if(accepted.Digest!=digest){obligations.Add("FILL_CORRECTION:"+f.Digest);conflict=true;}
                    continue;
                }
                try{candidate=NavalCashRiskPolicy.PostFill(candidate,fill,_definition);}
                catch(Exception ex) when(ex is InvalidOperationException or OverflowException){obligations.Add("ECONOMICS_UNSUPPORTED:"+f.Digest);continue;}
            }
            var orders=pass.Orders.ToArray();var retainedOrders=x.Orders.Concat(orders).GroupBy(p=>p.Digest).Select(g=>g.Last()).ToArray();var protection=pass.Protection.ToArray();
            foreach(var bill in pass.Bills)
            {
                bool linked=raw.Any(f=>f.Instrument==bill.Instrument&&f.OrderId==bill.OrderId&&f.ClientId==bill.ClientId&&f.TradeId==bill.TradeId&&
                    bill.Type=="2"&&f.FeeCurrency==bill.Currency&&f.SignedFee==bill.SignedFee);
                if(!linked)obligations.Add("UNLINKED_BILL:"+bill.Digest);
            }
            foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction&&i.Stage is NavalExecutionStage.Dispatching or NavalExecutionStage.SubmitUnknown or NavalExecutionStage.Accepted))
            {
                var r=candidate.Reservations.Single(r=>r.Id==i.ReservationId);
                var order=orders.SingleOrDefault(o=>o.Instrument==i.Instrument&&o.ClientId==i.ClientId&&(i.Outcome?.OrderId==null||o.OrderId==i.Outcome.OrderId));
                var owned=raw.Where(f=>f.Instrument==i.Instrument&&f.ClientId==i.ClientId&&f.OrderId==order?.OrderId).GroupBy(f=>f.TradeId).Select(g=>g.Last()).ToArray();
                decimal gross=candidate.Fills.Where(f=>f.Fill.ReservationId==r.Id&&!f.Fill.Reduction).Sum(f=>f.Fill.Quantity);
                bool corroborated=owned.All(f=>pass.Bills.Any(v=>v.Type=="2"&&v.TradeId==f.TradeId&&v.Instrument==f.Instrument&&v.OrderId==f.OrderId&&v.ClientId==f.ClientId&&v.Currency==f.FeeCurrency&&v.SignedFee==f.SignedFee));
                bool terminal=order!=null&&order.State=="filled"&&order.Quantity==r.Quantity&&order.Accumulated==r.Quantity&&gross==r.Quantity&&owned.Length>0&&
                    order.SignedFee==owned.Sum(f=>f.SignedFee)&&owned.All(f=>f.SignedFee!=null&&f.FeeCurrency==order.FeeCurrency)&&corroborated;
                bool zeroCanceled=order!=null&&NavalBrokerSettlement.ZeroFillCanceled(i,order,raw);
                if(!zeroCanceled&&(!terminal||_brokerAuthority?.ProtectionProfile==null))obligations.Add("ENTRY_FEE_FINALITY_UNPROVEN:"+i.Id);
                // An attached request alone never establishes an observed live stop/take-profit.
                bool protectedNow=order?.AlgoId!=null&&protection.Any(p=>p.Instrument==i.Instrument&&p.ParentOrderId==order.OrderId&&p.AlgoId==order.AlgoId&&
                    p.AttachmentClientId==order.AttachmentClientId&&p.State=="live"&&p.Quantity==r.Inventory&&p.Stop==r.Intent.StopPrice&&p.TakeProfit==r.Intent.TakeProfitPrice);
                if(r.Inventory>0&&!protectedNow)obligations.Add("PROTECTION_GAP:"+i.ReservationId);
                if(terminal&&_brokerAuthority?.ProtectionProfile!=null&&!conflict)
                {
                    settledIntents.Add(i.Id);
                    candidate=candidate with{Reservations=candidate.Reservations.Select(p=>p.Id==r.Id?p with{Pending=0,TerminalCoverage=true,Stage=p.Inventory>0?NavalReservationStage.Confirmed:p.Stage}:p).ToArray()};
                }
            }
            var expected=new Dictionary<string,decimal>(StringComparer.Ordinal){{b.Baseline.Binding.Account.QuoteCurrency,candidate.Accounts.Single().Cash}};
            foreach(var instrument in _definition.Catalog)
            {decimal inventory=candidate.Reservations.Where(r=>r.Intent.Instrument==instrument.Instrument).Sum(r=>r.Inventory);if(inventory!=0)expected.Add(instrument.BaseCurrency,inventory);}
            bool balancesMatch=pass.Balances.Count==expected.Count&&pass.Balances.All(v=>expected.TryGetValue(v.Currency,out var cash)&&v.Cash==cash&&v.Equity==cash&&v.Available+v.Frozen==cash&&
                v.Liability==0&&v.Interest==0&&v.CrossLiability==0&&v.IsolatedLiability==0&&v.StrategyEquity==0&&v.FixedBalance==0);
            if(!balancesMatch)obligations.Add("BROKER_BALANCE_DISCREPANCY");
            var clock=(INavalBrokerClock)_clock;bool fresh=clock.UtcNow>=pass.CaptureUtc&&clock.UtcNow-pass.CaptureUtc<TimeSpan.FromSeconds(30)&&clock.Timestamp>=pass.CaptureTimestamp&&
                (decimal)(clock.Timestamp-pass.CaptureTimestamp)/clock.TimestampFrequency<30&&pass.Quotes.Count==_definition.Catalog.Count&&pass.Quotes.Values.All(ValidQuote);
            if(!pass.CollectionClosed||!fresh)obligations.Add("BROKER_COVERAGE_INCOMPLETE");
            // A cash FOK sell that conclusively canceled with zero fills is NOT a sale.
            // Retain the inventory/holds and permit a later explicit operation with a fresh
            // ID only after closed history, original order, fees and exact balances agree.
            if(pass.CollectionClosed&&fresh&&balancesMatch&&!conflict)
            foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.CancelProtection&&i.DispatchRevision!=null))
                if(pass.Protection.Any(p=>NavalBrokerSettlement.CanceledProtection(i,p)))settledIntents.Add(i.Id);
            if(pass.CollectionClosed&&fresh&&balancesMatch&&!conflict&&obligations.All(o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal)))
            foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null))
            {
                bool zero=orders.Any(o=>NavalBrokerSettlement.ZeroFillCanceled(i,o,raw));
                if(zero||orders.Any(o=>NavalBrokerSettlement.FilledReduction(i,o,raw,bills)))settledIntents.Add(i.Id);
                if(zero&&!i.Reduction)candidate=candidate with{Reservations=candidate.Reservations.Select(r=>r.Id==i.ReservationId?r with{Pending=0,TerminalCoverage=true,Stage=NavalReservationStage.Terminal}:r).ToArray()};
            }
            foreach(var reservation in candidate.Reservations.Where(r=>r.ReductionUnsettled))
            {
                var sells=candidate.Fills.Where(p=>p.Fill.ReservationId==reservation.Id&&p.Fill.Reduction).Select(p=>p.Fill).ToArray();
                var orderIds=sells.SelectMany(f=>raw.Where(v=>v.Instrument==f.Instrument&&v.TradeId==f.TradeId&&v.Side=="sell").Select(v=>v.OrderId)).Distinct().ToArray();
                bool settled=orderIds.Length>0&&orderIds.All(id=>
                {
                    var o=orders.SingleOrDefault(o=>o.Instrument==reservation.Intent.Instrument&&o.OrderId==id);
                    var trades=raw.Where(v=>v.Instrument==reservation.Intent.Instrument&&v.OrderId==id&&v.Side=="sell").GroupBy(v=>v.TradeId).Select(g=>g.Last()).ToArray();
                    return o?.State=="filled"&&o.Quantity==o.Accumulated&&o.Accumulated==trades.Sum(v=>v.Quantity)&&o.SignedFee==trades.Sum(v=>v.SignedFee)&&
                        trades.All(v=>v.Quantity!=null&&v.SignedFee!=null&&v.FeeCurrency==o.FeeCurrency&&pass.Bills.Any(bill=>bill.Type=="2"&&bill.Instrument==v.Instrument&&bill.OrderId==v.OrderId&&bill.TradeId==v.TradeId&&bill.Currency==v.FeeCurrency&&bill.SignedFee==v.SignedFee));
                });
                if(!settled||_brokerAuthority?.ProtectionProfile==null)obligations.Add("REDUCTION_FINALITY_UNPROVEN:"+reservation.Id);
                else if(obligations.Count==0&&balancesMatch&&fresh)candidate=candidate with{Reservations=candidate.Reservations.Select(r=>r.Id==reservation.Id?r with{ReductionUnsettled=false,Stage=r.Inventory==0?NavalReservationStage.Terminal:r.Stage}:r).ToArray()};
            }
            _brokerQuotes=NavalJson.Clone(pass.Quotes);_brokerTimestamp=pass.CaptureTimestamp;
            if(pass.Quotes.Count==_definition.Catalog.Count)candidate=candidate with{Marks=pass.Quotes.ToDictionary(p=>p.Key,p=>p.Value.Bid)};
            candidate=NavalCashRiskPolicy.Refresh(candidate,_definition,clock.UtcNow,obligations.Count==0);
            e.NavalBroker=b with{Operational=x with{SettledIntents=settledIntents.ToArray(),FillEvidence=raw,Bills=bills,Orders=retainedOrders,Protection=knownProtection,Obligations=obligations.OrderBy(p=>p,StringComparer.Ordinal).ToArray(),Evidence=pass.Evidence.ToArray()}};
            candidate=candidate with{Reservations=candidate.Reservations.Select(r=>x.Intents.Any(i=>i.ReservationId==r.Id&&i.Stage==NavalExecutionStage.SubmitUnknown&&!settledIntents.Contains(i.Id))?r with{Stage=NavalReservationStage.Uncertain}:r.Stage==NavalReservationStage.Uncertain&&r.TerminalCoverage?r with{Stage=r.Inventory>0?NavalReservationStage.Confirmed:NavalReservationStage.Terminal}:r).ToArray()};
            e.Naval=candidate;
            if(obligations.Count>0)return StopCandidate(e,obligations.First(),conflict);
            candidate=candidate with{Generation=Math.Max(_restartGeneration,candidate.Generation),Sequence=checked(candidate.Sequence+1),ReconciledUtc=pass.CaptureUtc,Reason="BROKER_RECONCILED"};
            var result=Commit(e,candidate,NavalMutationStatus.Applied,"BROKER_ECONOMICS_RECONCILED",Maintenance());
            if(result.Status==NavalMutationStatus.Applied)_ready=true;return result;
        }
    }
}
