using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Apex.Core.Naval;

// Original signed fee and observed actual quote retained separately from the once-only posting.
public sealed record NavalBrokerRawFill(string Instrument,string OrderId,string ClientId,string TradeId,string BillId,string Side,
    decimal? Quantity,decimal? Price,decimal? SignedFee,string? FeeCurrency,string? ActualQuote,DateTimeOffset? TradeUtc,string Digest);
public sealed record NavalBrokerRawBill(string Id,string? Instrument,string? OrderId,string? ClientId,string? TradeId,string Type,string Currency,
    decimal? SignedFee,decimal? BalanceChange,DateTimeOffset? SourceUtc,string Digest);
public sealed record NavalBrokerOrderProof(string Instrument,string OrderId,string ClientId,string? State,decimal? Quantity,decimal? Accumulated,
    decimal? SignedFee,string? FeeCurrency,string? ActualQuote,string? AttachmentClientId,string? AlgoId,string Digest)
{
    // Legacy order proofs stored attachAlgoId in AlgoId. An absent extension stays
    // absent in canonical JSON, preserving sealed pre-fix state without guessing.
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AttachmentId { get; init; }
}
public sealed record NavalBrokerProtection(string Instrument,string ParentOrderId,string AttachmentClientId,string AlgoId,string? State,
    decimal? Quantity,decimal? Stop,decimal? TakeProfit,IReadOnlyList<string> ChildOrderIds,string Digest)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AttachmentId { get; init; }
}
internal sealed record NavalBrokerEconomicPass(DateTimeOffset CaptureUtc,long CaptureTimestamp,bool CollectionClosed,
    IReadOnlyList<NavalBrokerRawFill> Fills,IReadOnlyList<NavalBrokerRawBill> Bills,IReadOnlyList<NavalBrokerOrderProof> Orders,
    IReadOnlyList<NavalBrokerProtection> Protection,IReadOnlyList<NavalBrokerBalance> Balances,
    IReadOnlyDictionary<string,NavalBrokerQuote> Quotes,IReadOnlyList<NavalBrokerSurface> Evidence,IReadOnlyList<string> Problems)
{
    internal CurrentMarketFundingEvidenceV1? CurrentMarketFunding { get; init; }
    internal NavalFiniteOperatorPremise? OperatorPremise { get; init; }
    internal Guid ObservationCapture { get; init; }
    internal NavalStopOwnedReduction? OwnedReduction { get; init; }
}
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
            bool recovering=pass.OwnedReduction!=null&&OwnedCapability(session,pass.OwnedReduction,e);
            if(pass.OwnedReduction!=null&&!recovering)return Result(s,NavalMutationStatus.Refused,"OWNED_REDUCTION_CAPABILITY_REQUIRED");
            bool economicProfile=_brokerAuthority?.ProtectionProfile!=null||recovering;
            var recoverySpec=recovering?_definition.Catalog.Single(spec=>spec.Instrument==x.Intents.Single(i=>i.Id==pass.OwnedReduction!.ParentId).Instrument):null;
            var paired=recovering&&pass.CollectionClosed?OwnedPairedTradeBills(pass,x,pass.OwnedReduction!,recoverySpec!,b.Baseline.Binding.Account.QuoteCurrency):
                economicProfile&&pass.CollectionClosed?_definition.Catalog.SelectMany(spec=>ExactPairedTradeBills(pass,x,spec,b.Baseline.Binding.Account.QuoteCurrency,_=>true)).ToHashSet():new HashSet<NavalBrokerRawBill>();
            // Reclassify only the exact formerly-unlinked payload now proven by a
            // fresh closed collection, signed parent and allocated forwarded client.
            // Never clear unrelated foreign digests or infer generated IDs from legacy data.
            var resolved=pass.CollectionClosed?pass.Protection.Where(p=>p.AttachmentId!=null&&pass.Orders.Any(o=>
                o.Instrument==p.Instrument&&o.OrderId==p.ParentOrderId&&o.AttachmentId==p.AttachmentId&&o.AlgoId==p.AlgoId&&o.AttachmentClientId==p.AttachmentClientId&&
                x.Intents.Any(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction&&i.DispatchRevision!=null&&i.Instrument==o.Instrument&&i.ClientId==o.ClientId&&
                    (i.Outcome?.OrderId==null||i.Outcome.OrderId==o.OrderId)&&x.IdAllocations.SelectMany(a=>a.Ids).Any(id=>id.ReservationId==i.ReservationId&&id.Role==NavalClientIdRole.ProtectionAlgo&&id.ClientId==p.AttachmentClientId))))
                .Select(p=>"FOREIGN_OR_UNLINKED_ALGO:"+p.Digest).ToHashSet(StringComparer.Ordinal):new HashSet<string>(StringComparer.Ordinal);
            if(recovering&&pass.CollectionClosed)
            foreach(var bill in pass.Bills)
            if(x.Bills.Contains(bill)&&pass.Fills.Any(f=>OwnedRecoveryBill(bill,f)&&x.Intents.Any(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null&&i.Instrument==f.Instrument&&i.ClientId==f.ClientId&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==f.OrderId))&&
                pass.Orders.Any(o=>o.Instrument==f.Instrument&&o.OrderId==f.OrderId&&o.ClientId==f.ClientId&&o.State=="filled")))resolved.Add("UNLINKED_BILL:"+bill.Digest);
            if(!recovering)foreach(var bill in paired.Where(x.Bills.Contains))resolved.Add("UNLINKED_BILL:"+bill.Digest);
            var obligations=new HashSet<string>(x.Obligations.Where(p=>(p.StartsWith("FOREIGN_",StringComparison.Ordinal)||p.StartsWith("UNLINKED_BILL:",StringComparison.Ordinal)||p.StartsWith("FILL_CORRECTION:",StringComparison.Ordinal))&&!resolved.Contains(p)).Concat(pass.Problems),StringComparer.Ordinal);
            var knownProtection=x.Protection.Concat(pass.Protection).Select((p,n)=>(p,n)).GroupBy(v=>NavalJson.Hash(v.p)).Select(g=>g.Last()).OrderBy(v=>v.n).Select(v=>v.p).ToArray();
            var raw=x.FillEvidence.Concat(pass.Fills).Distinct().ToArray();var bills=x.Bills.Concat(pass.Bills).Distinct().ToArray();
            if(raw.Length>8192||bills.Length>16384||x.Intents.Count>4096)return StopCandidate(e,"BROKER_EVIDENCE_CAPACITY");
            if(recovering)
            foreach(var retained in x.Obligations)
                if(!resolved.Contains(retained)&&!OwnedRecomputedObligation(retained,pass.OwnedReduction!))obligations.Add(retained);
            var candidate=s;bool conflict=false;var settledIntents=x.SettledIntents.ToHashSet(StringComparer.Ordinal);var recoveryTerminal=new System.Collections.Generic.List<string>();
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
                bool linked=paired.Contains(bill)||raw.Any(f=>(!recovering||OwnedRecoveryBill(bill,f))&&f.Instrument==bill.Instrument&&f.OrderId==bill.OrderId&&(f.ClientId==bill.ClientId||recovering&&string.IsNullOrEmpty(bill.ClientId))&&f.TradeId==bill.TradeId&&
                    bill.Type=="2"&&f.FeeCurrency==bill.Currency&&f.SignedFee==bill.SignedFee);
                if(!linked)obligations.Add("UNLINKED_BILL:"+bill.Digest);
            }
            foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction&&i.Stage is NavalExecutionStage.Dispatching or NavalExecutionStage.SubmitUnknown or NavalExecutionStage.Accepted))
            {
                var r=candidate.Reservations.Single(r=>r.Id==i.ReservationId);
                var order=orders.SingleOrDefault(o=>o.Instrument==i.Instrument&&o.ClientId==i.ClientId&&(i.Outcome?.OrderId==null||o.OrderId==i.Outcome.OrderId));
                var owned=raw.Where(f=>f.Instrument==i.Instrument&&f.ClientId==i.ClientId&&f.OrderId==order?.OrderId).GroupBy(f=>f.TradeId).Select(g=>g.Last()).ToArray();
                decimal gross=candidate.Fills.Where(f=>f.Fill.ReservationId==r.Id&&!f.Fill.Reduction).Sum(f=>f.Fill.Quantity);
                bool corroborated=owned.All(f=>pass.Bills.Any(v=>recovering?OwnedRecoveryBill(v,f):v.Type=="2"&&v.TradeId==f.TradeId&&v.Instrument==f.Instrument&&v.OrderId==f.OrderId&&v.ClientId==f.ClientId&&v.Currency==f.FeeCurrency&&v.SignedFee==f.SignedFee));
                bool terminal=order!=null&&order.State=="filled"&&order.Quantity==r.Quantity&&order.Accumulated==r.Quantity&&gross==r.Quantity&&owned.Length>0&&
                    order.SignedFee==owned.Sum(f=>f.SignedFee)&&owned.All(f=>f.SignedFee!=null&&f.FeeCurrency==order.FeeCurrency)&&corroborated;
                bool zeroCanceled=order!=null&&NavalBrokerSettlement.ZeroFillCanceled(i,order,raw);
                if(!zeroCanceled&&(!terminal||!economicProfile))obligations.Add("ENTRY_FEE_FINALITY_UNPROVEN:"+i.Id);
                // An attached request alone never establishes an observed live stop/take-profit.
                bool protectedNow=order?.AlgoId!=null&&order.AttachmentId!=null&&protection.Any(p=>p.Instrument==i.Instrument&&p.ParentOrderId==order.OrderId&&p.AlgoId==order.AlgoId&&p.AttachmentId==order.AttachmentId&&
                    p.AttachmentClientId==order.AttachmentClientId&&p.State=="live"&&p.Quantity==r.Inventory&&p.Stop==r.Intent.StopPrice&&p.TakeProfit==r.Intent.TakeProfitPrice);
                if(r.Inventory>0&&!protectedNow)obligations.Add("PROTECTION_GAP:"+i.ReservationId);
                if(terminal&&economicProfile&&!conflict)
                {
                    if(recovering)recoveryTerminal.Add(i.Id);
                    else{settledIntents.Add(i.Id);candidate=candidate with{Reservations=candidate.Reservations.Select(p=>p.Id==r.Id?p with{Pending=0,TerminalCoverage=true,Stage=p.Inventory>0?NavalReservationStage.Confirmed:p.Stage}:p).ToArray()};}
                }
            }
            bool ordinary=_brokerAuthority?.OrdinaryDemoPolicy==true&&_brokerAuthority is INavalFiniteDemoAuthority{FiniteDemo:false}&&b.Baseline.Binding.Account.Environment==NavalEnvironment.OkxDemo;
            bool market=ordinary||b.Baseline.Observations.CurrentMarketFunding!=null;
            bool fundingValid=!market;
            if(market)
            {
                var f=pass.CurrentMarketFunding;var p=pass.OperatorPremise;var binding=b.Baseline.Binding;var timing=(INavalBrokerClock)_clock;
                fundingValid=ordinary?f!=null&&p==null&&pass.Evidence.Contains(f.Response)&&NavalCurrentMarketFunding.ValidateOrdinary(f,pass.Balances,binding.Account.AccountId,binding.MainUid,binding.CredentialBindingId,binding.CredentialFingerprint,_brokerAuthority!.SessionId,pass.ObservationCapture)&&NavalCurrentMarketFunding.FreshOwnedRecovery(f,timing.UtcNow,timing.Timestamp,timing.TimestampFrequency,pass.CaptureUtc,pass.CaptureTimestamp):
                    f!=null&&p==b.Baseline.Observations.OperatorPremise&&pass.Evidence.Contains(f.Response)&&(recovering?
                    NavalCurrentMarketFunding.ValidateOwnedRecovery(f,p,pass.Balances,binding.Account.AccountId,binding.MainUid,binding.CredentialBindingId,binding.CredentialFingerprint,_brokerAuthority!.SessionId,pass.ObservationCapture)&&NavalCurrentMarketFunding.FreshOwnedRecovery(f,timing.UtcNow,timing.Timestamp,timing.TimestampFrequency,pass.CaptureUtc,pass.CaptureTimestamp):
                    NavalCurrentMarketFunding.Validate(f,p,pass.Balances,binding.Account.AccountId,binding.MainUid,binding.CredentialBindingId,binding.CredentialFingerprint,_brokerAuthority!.SessionId,pass.ObservationCapture)&&NavalCurrentMarketFunding.Fresh(f,p!,timing.UtcNow,timing.Timestamp,timing.TimestampFrequency,pass.CaptureUtc,pass.CaptureTimestamp));
                if(!fundingValid)obligations.Add("CURRENT_MARKET_FUNDING_UNPROVEN");
            }
            else if(pass.CurrentMarketFunding!=null||pass.OperatorPremise!=null){fundingValid=false;obligations.Add("UNEXPECTED_FUNDING_EVIDENCE");}
            var expected=new Dictionary<string,decimal>(StringComparer.Ordinal){{b.Baseline.Binding.Account.QuoteCurrency,candidate.Accounts.Single().Cash}};
            if(b.Baseline.Observations.FiniteAllocation is {} allocation)
                foreach(var excluded in allocation.Excluded)expected.Add(excluded.Currency,excluded.Cash!.Value);
            foreach(var instrument in _definition.Catalog)
            {decimal inventory=candidate.Reservations.Where(r=>r.Intent.Instrument==instrument.Instrument).Sum(r=>r.Inventory);if(inventory!=0)expected[instrument.BaseCurrency]=checked(expected.GetValueOrDefault(instrument.BaseCurrency)+inventory);}
            bool balancesMatch=fundingValid&&pass.Balances.Select(v=>v.Currency).Distinct(StringComparer.Ordinal).Count()==pass.Balances.Count&&pass.Balances.Count==expected.Count&&pass.Balances.All(v=>expected.TryGetValue(v.Currency,out var cash)&&v.Cash==cash&&v.Equity==cash&&v.Available+v.Frozen==cash&&
                (market?NavalCurrentMarketFunding.RawDebtCompatible(v):v.Liability==0&&v.Interest==0&&v.CrossLiability==0&&v.IsolatedLiability==0)&&v.StrategyEquity==0&&v.FixedBalance==0);
            if(b.Baseline.Observations.FiniteAllocation is {} finite)
            {
                // Initial zero rows are explicit observations and must remain present. A
                // previously absent test base may appear only with proven positive own inventory.
                // No other missing/extra/zero row is normalized by the parser or this owner.
                decimal ownPendingQuote=0;
                foreach(var reservation in candidate.Reservations)
                    if(orders.Any(o=>o.Instrument==reservation.Intent.Instrument&&(o.State is "live" or "partially_filled")&&x.Intents.Any(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction&&i.DispatchRevision!=null&&i.ReservationId==reservation.Id&&i.ClientId==o.ClientId&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==o.OrderId))))
                        ownPendingQuote=checked(ownPendingQuote+NavalCashRiskPolicy.BuyDebit(reservation.Pending,reservation.Intent.EntryPriceCeiling,_definition.Fees));
                var ownInventory=_definition.Catalog.ToDictionary(i=>i.BaseCurrency,i=>candidate.Reservations.Where(r=>r.Intent.Instrument==i.Instrument).Sum(r=>r.Inventory),StringComparer.Ordinal);
                balancesMatch=balancesMatch&&pass.Balances.All(v=>
                    NavalCurrentMarketFunding.Balance(v with{Available=v.Cash,Frozen=0,OrderFrozen=0},market)&&v.SourceUtc<=_clock.UtcNow&&v.Available is >=0&&v.Frozen is >=0&&v.OrderFrozen is >=0&&v.OrderFrozen<=v.Frozen&&
                    // Only exact own pending quote / own post-fee inventory may be frozen.
                    // Excluded holdings remain byte-for-economic-field unchanged below.
                    (v.Currency==finite.Quote?v.Frozen<=ownPendingQuote:v.Frozen<=ownInventory.GetValueOrDefault(v.Currency)));
                foreach(var excluded in finite.Excluded.Where(v=>x.OrdinaryCatalog!=null||v.Currency!=b.Baseline.Observations.Catalog.Single().BaseCurrency))
                {
                    var observed=pass.Balances.FirstOrDefault(v=>v.Currency==excluded.Currency);
                    if(observed!=null&&ownInventory.TryGetValue(excluded.Currency,out var inventory))
                        observed=observed with{Cash=observed.Cash-inventory,Equity=observed.Equity-inventory,Available=observed.Available+observed.Frozen-inventory,Frozen=0,OrderFrozen=0};
                    if(observed==null||(observed with{SourceUtc=excluded.SourceUtc})!=excluded)
                    {obligations.Add("FINITE_EXCLUDED_BALANCE_CHANGED");balancesMatch=false;}
                }
            }
            if(!balancesMatch)obligations.Add("BROKER_BALANCE_DISCREPANCY");
            var clock=(INavalBrokerClock)_clock;bool fresh=clock.UtcNow>=pass.CaptureUtc&&clock.UtcNow-pass.CaptureUtc<TimeSpan.FromSeconds(30)&&clock.Timestamp>=pass.CaptureTimestamp&&
                (decimal)(clock.Timestamp-pass.CaptureTimestamp)/clock.TimestampFrequency<30&&pass.Quotes.Count==_definition.Catalog.Count&&pass.Quotes.Values.All(ValidQuote);
            if(!pass.CollectionClosed||!fresh)obligations.Add("BROKER_COVERAGE_INCOMPLETE");
            if(recovering&&pass.CollectionClosed&&pass.Problems.Count==0&&fresh&&balancesMatch&&!conflict&&recoveryTerminal.Contains(pass.OwnedReduction!.ParentId)&&
                OwnedObservedProtection(pass,x,pass.OwnedReduction!,candidate.Reservations.Single(r=>r.Id==pass.OwnedReduction!.ReservationId),recoverySpec!))
            {
                var cap=pass.OwnedReduction!;
                var ownBillStatuses=paired.Where(x.Bills.Contains).Select(v=>"UNLINKED_BILL:"+v.Digest).ToHashSet(StringComparer.Ordinal);
                // Nothing is cleared at pass start. Only this complete current financial
                // and protection proof may supersede these observation-only statuses.
                // Every unrelated digest/conflict remains a veto, including old own bills
                // absent from the current proof. Bills are evidence, never ledger postings.
                if(obligations.All(o=>o=="PROTECTION_GAP:"+cap.ReservationId||OwnedObsoleteObservation(o,cap)||ownBillStatuses.Contains(o)))
                {
                    obligations.ExceptWith(ownBillStatuses);
                    obligations.RemoveWhere(o=>OwnedObsoleteObservation(o,cap));
                }
            }
            if(recovering&&pass.CollectionClosed&&fresh&&balancesMatch&&!conflict&&obligations.All(o=>o=="PROTECTION_GAP:"+pass.OwnedReduction!.ReservationId))
            foreach(string id in recoveryTerminal)
            {settledIntents.Add(id);candidate=candidate with{Reservations=candidate.Reservations.Select(r=>r.Id==pass.OwnedReduction!.ReservationId?r with{Pending=0,TerminalCoverage=true,Stage=r.Inventory>0?NavalReservationStage.Confirmed:NavalReservationStage.Terminal}:r).ToArray()};}
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
                if(!settled||!economicProfile)obligations.Add("REDUCTION_FINALITY_UNPROVEN:"+reservation.Id);
                else if(obligations.All(o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal))&&balancesMatch&&fresh)candidate=candidate with{Reservations=candidate.Reservations.Select(r=>r.Id==reservation.Id?r with{ReductionUnsettled=false,Stage=r.Inventory==0?NavalReservationStage.Terminal:r.Stage}:r).ToArray()};
            }
            _brokerQuotes=NavalJson.Clone(pass.Quotes);_brokerTimestamp=pass.CaptureTimestamp;
            if(pass.Quotes.Count==_definition.Catalog.Count)candidate=candidate with{Marks=pass.Quotes.ToDictionary(p=>p.Key,p=>p.Value.Bid)};
            if(x.OrdinaryDemo is {Dust:not null} admission)
            {
                var pinned=candidate.Reservations.Single(r=>r.Id==admission.Dust.Reservation);
                if(!NavalOrdinaryDemoPolicy.Pinned(e,pinned)||pinned.Inventory!=admission.Dust.Quantity||pinned.AllocatedCost!=admission.Dust.Cost)obligations.Add("PINNED_DUST_CHANGED");
                else if(pass.Quotes.TryGetValue(pinned.Intent.Instrument,out var valuation)&&ValidQuote(valuation))candidate=candidate with{Reservations=candidate.Reservations.Select(r=>r.Id==pinned.Id?r with{QuarantineMark=valuation.Ask}:r).ToArray()};
            }
            candidate=NavalCashRiskPolicy.Refresh(candidate,_definition,clock.UtcNow,!recovering&&!obligations.Any(o=>NavalOrdinaryDemoPolicy.Blocking(e,o)));
            e.NavalBroker=b with{Operational=x with{SettledIntents=settledIntents.ToArray(),FillEvidence=raw,Bills=bills,Orders=retainedOrders,Protection=knownProtection,Obligations=obligations.OrderBy(p=>p,StringComparer.Ordinal).ToArray(),Evidence=pass.Evidence.ToArray(),LastBalances=pass.Balances.ToArray(),LastBalanceCaptureUtc=pass.CaptureUtc,LastCurrentMarketFunding=pass.CurrentMarketFunding}};
            candidate=candidate with{Reservations=candidate.Reservations.Select(r=>x.Intents.Any(i=>i.ReservationId==r.Id&&i.Stage==NavalExecutionStage.SubmitUnknown&&!settledIntents.Contains(i.Id))?r with{Stage=NavalReservationStage.Uncertain}:r.Stage==NavalReservationStage.Uncertain&&r.TerminalCoverage?r with{Stage=r.Inventory>0?NavalReservationStage.Confirmed:NavalReservationStage.Terminal}:r).ToArray()};
            e.Naval=candidate;
            if(obligations.Any(o=>NavalOrdinaryDemoPolicy.Blocking(e,o)))return StopCandidate(e,obligations.First(),conflict,strategyPause:!recovering&&!conflict&&pass.CollectionClosed&&pass.Problems.Count==0&&fresh&&balancesMatch&&StrategyProtectionPause(session,e,obligations));
            if(recovering){_ready=false;return Commit(e,candidate with{Mode="STOP",Reason="STOP_OWNED_ECONOMICS_RECONCILED"},NavalMutationStatus.Applied,"STOP_OWNED_ECONOMICS_RECONCILED",Maintenance());}
            candidate=candidate with{Generation=Math.Max(_restartGeneration,candidate.Generation),Sequence=checked(candidate.Sequence+1),ReconciledUtc=pass.CaptureUtc,Reason="BROKER_RECONCILED"};
            var result=Commit(e,candidate,NavalMutationStatus.Applied,"BROKER_ECONOMICS_RECONCILED",Maintenance());
            if(result.Status==NavalMutationStatus.Applied)_ready=true;return result;
        }
    }
    private static bool OwnedRecomputedObligation(string reason,NavalStopOwnedReduction cap)=>
        reason is "RECOVERY_NO_ENTRY_OR_FUNDING_AUTHORITY" or "CURRENT_MARKET_FUNDING_UNPROVEN" or "BROKER_BALANCE_DISCREPANCY" or "BROKER_COVERAGE_INCOMPLETE" or "RECOVERY_PROTECTION_UNPROVEN" or "RECOVERY_PROTECTION_IDENTITY_CONFLICT" or "FINITE_EXCLUDED_BALANCE_CHANGED"||
        reason=="ENTRY_FEE_FINALITY_UNPROVEN:"+cap.ParentId||reason=="ORIGINAL_ORDER_UNKNOWN:"+cap.ParentId||reason=="PROTECTION_UNKNOWN:"+cap.ParentId||reason=="PROTECTION_GAP:"+cap.ReservationId||
        new[]{"Pending","Fills","FillsArchive","Bills","BillsArchive"}.Any(s=>reason=="HISTORY_INCOMPLETE:"+s);
    private static bool OwnedObsoleteObservation(string reason,NavalStopOwnedReduction cap)=>
        reason=="OWNED_EXACT_ORDER_UNKNOWN:"+cap.ParentId||reason is "OWNED_PROTECTION_TYPE_CONFIG_OR_IDENTITY_UNSUPPORTED_NO_SELL" or "OWNED_FOREIGN_OR_UNLINKED_PENDING_ALGO_NO_SELL";
    private static bool OwnedObservedProtection(NavalBrokerEconomicPass pass,NavalBrokerOperationalState x,NavalStopOwnedReduction cap,NavalReservation r,NavalInstrument spec)
    {
        var parent=x.Intents.Single(i=>i.Id==cap.ParentId);
        var orders=pass.Orders.Where(o=>o.Instrument==parent.Instrument&&o.ClientId==parent.ClientId&&(parent.Outcome?.OrderId==null||parent.Outcome.OrderId==o.OrderId)).ToArray();
        var allocated=x.IdAllocations.SelectMany(a=>a.Ids).Where(id=>id.ReservationId==cap.ReservationId&&id.Role==NavalClientIdRole.ProtectionAlgo).ToArray();
        if(orders.Length!=1||allocated.Length!=1||pass.Protection.Count!=1)return false;
        var o=orders[0];var p=pass.Protection[0];
        try
        {
            decimal net=checked(o.Accumulated!.Value+(o.FeeCurrency==spec.BaseCurrency?o.SignedFee!.Value:0)),lot=decimal.Floor(net/spec.Quantum)*spec.Quantum;
            return o.State=="filled"&&o.Quantity==r.Quantity&&o.Accumulated==r.Quantity&&o.AttachmentClientId==allocated[0].ClientId&&!string.IsNullOrEmpty(o.AttachmentId)&&!string.IsNullOrEmpty(o.AlgoId)&&
                p.Instrument==spec.Instrument&&p.ParentOrderId==o.OrderId&&p.AttachmentClientId==allocated[0].ClientId&&p.AttachmentId==o.AttachmentId&&p.AlgoId==o.AlgoId&&
                p.State is "live" or "canceled"&&p.ChildOrderIds.Count==0&&p.Stop==r.Intent.StopPrice&&p.TakeProfit==r.Intent.TakeProfitPrice&&
                (p.Quantity==net||net>0&&lot>0&&lot>=spec.MinimumQuantity&&p.Quantity==lot&&net-lot>=0&&net-lot<spec.Quantum)&&
                !x.Protection.Any(v=>v.AttachmentClientId==p.AttachmentClientId&&v.AttachmentId!=null&&(v.Instrument!=p.Instrument||v.ParentOrderId!=p.ParentOrderId||v.AttachmentId!=p.AttachmentId||v.AlgoId!=p.AlgoId));
        }
        catch(Exception e) when(e is InvalidOperationException or OverflowException){return false;}
    }
    private static HashSet<NavalBrokerRawBill> OwnedPairedTradeBills(NavalBrokerEconomicPass pass,NavalBrokerOperationalState x,NavalStopOwnedReduction cap,NavalInstrument spec,string quote)
        =>ExactPairedTradeBills(pass,x,spec,quote,i=>i.ReservationId==cap.ReservationId&&(i.Id==cap.ParentId&&!i.Reduction||i.Id=="owned-exit-"+cap.ParentId&&i.Reduction));
    // Same exact two-leg corroboration for durably dispatched ordinary own orders.
    // This consumes evidence only; neither bills nor the selector grant dispatch authority.
    private static HashSet<NavalBrokerRawBill> ExactPairedTradeBills(NavalBrokerEconomicPass pass,NavalBrokerOperationalState x,NavalInstrument spec,string quote,Func<NavalBrokerIntent,bool> selected)
    {
        var proven=new HashSet<NavalBrokerRawBill>();
        if(!pass.CollectionClosed||spec.QuoteCurrency!=quote||spec.BaseCurrency==quote)return proven;
        var historyFills=x.FillEvidence.Concat(pass.Fills).Distinct().ToArray();var historyBills=x.Bills.Concat(pass.Bills).Distinct().ToArray();
        foreach(var f in pass.Fills)
        {
            if(f.Instrument!=spec.Instrument||f.ActualQuote!=quote||string.IsNullOrEmpty(f.OrderId)||string.IsNullOrEmpty(f.TradeId)||string.IsNullOrEmpty(f.ClientId)||string.IsNullOrEmpty(f.BillId)||
                f.TradeUtc==null||f.TradeUtc>pass.CaptureUtc||f.Quantity is not >0||f.Price is not >0||f.SignedFee is not <=0||f.FeeCurrency!=spec.BaseCurrency&&f.FeeCurrency!=quote||f.Side is not ("buy" or "sell"))continue;
            if(pass.Fills.Count(v=>v.Instrument==f.Instrument&&v.TradeId==f.TradeId)!=1||historyFills.Count(v=>v.Instrument==f.Instrument&&v.TradeId==f.TradeId)!=1)continue;
            var owners=x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null&&selected(i)&&
                i.Instrument==f.Instrument&&i.ClientId==f.ClientId&&(i.Outcome?.OrderId==null||i.Outcome.OrderId==f.OrderId)&&f.Side==(i.Reduction?"sell":"buy")).ToArray();
            var orders=pass.Orders.Where(o=>o.Instrument==f.Instrument&&o.OrderId==f.OrderId&&o.ClientId==f.ClientId).ToArray();
            if(owners.Length!=1||orders.Length!=1)continue;
            var o=orders[0];var trades=pass.Fills.Where(v=>v.Instrument==f.Instrument&&v.OrderId==f.OrderId&&v.ClientId==f.ClientId).ToArray();
            var legs=pass.Bills.Where(b=>b.Id==f.BillId||b.Instrument==f.Instrument&&b.OrderId==f.OrderId&&b.TradeId==f.TradeId).ToArray();
            if(legs.Length!=2||legs.Count(b=>b.Id==f.BillId)!=1||legs.Any(b=>string.IsNullOrEmpty(b.Id))||legs[0].Id==legs[1].Id||legs.Any(b=>historyBills.Count(v=>v.Id==b.Id)!=1))continue;
            if(legs.Any(b=>b.Type!="2"||b.Instrument!=f.Instrument||b.OrderId!=f.OrderId||b.TradeId!=f.TradeId||b.ClientId!=f.ClientId||b.SourceUtc!=f.TradeUtc))continue;
            try
            {
                decimal total=0,totalFee=0;bool complete=true;
                foreach(var trade in trades)
                    if(trade.Quantity is not >0||trade.SignedFee==null||trade.FeeCurrency!=o.FeeCurrency||trade.Side!=f.Side||
                        !OwnedExactAmount(total,trade.Quantity.Value,false,out total)||!OwnedExactAmount(totalFee,trade.SignedFee.Value,false,out totalFee)){complete=false;break;}
                if(!complete||o.State!="filled"||o.Quantity is not >0||o.Accumulated!=o.Quantity||o.ActualQuote!=quote||o.FeeCurrency!=f.FeeCurrency||o.SignedFee!=totalFee||total!=o.Quantity)continue;
                if(!OwnedExactAmount(f.Quantity.Value,f.Price.Value,true,out var gross)||gross<=0||
                    !OwnedExactAmount(f.Side=="buy"?f.Quantity.Value:-f.Quantity.Value,f.FeeCurrency==spec.BaseCurrency?f.SignedFee.Value:0,false,out var baseChange)||
                    !OwnedExactAmount(f.Side=="buy"?-gross:gross,f.FeeCurrency==quote?f.SignedFee.Value:0,false,out var quoteChange))continue;
                var anchor=legs.SingleOrDefault(b=>b.Id==f.BillId);var counter=legs.SingleOrDefault(b=>b.Id!=f.BillId);
                if(anchor==null||counter==null||anchor.Currency!=f.FeeCurrency||anchor.SignedFee!=f.SignedFee||anchor.BalanceChange!=(f.FeeCurrency==spec.BaseCurrency?baseChange:quoteChange)||
                    counter.Currency!=(f.FeeCurrency==spec.BaseCurrency?quote:spec.BaseCurrency)||counter.SignedFee!=0||counter.BalanceChange!=(f.FeeCurrency==spec.BaseCurrency?quoteChange:baseChange))continue;
                proven.Add(anchor);proven.Add(counter);
            }
            catch(OverflowException){continue;}
        }
        return proven;
    }
    // Decimal multiplication/addition can silently round without overflowing. Compare
    // the exact scaled-integer result; neither a lost quantum nor a guessed zero qualifies.
    private static bool OwnedExactAmount(decimal a,decimal b,bool multiply,out decimal result)
    {
        result=0;
        try
        {
            result=multiply?checked(a*b):checked(a+b);
            (BigInteger Value,int Scale) Parts(decimal d)
            {
                var bits=decimal.GetBits(d);var value=(BigInteger)(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
                return ((bits[3]&int.MinValue)!=0?-value:value,(bits[3]>>16)&255);
            }
            var x=Parts(a);var y=Parts(b);var z=Parts(result);int scale=multiply?x.Scale+y.Scale:Math.Max(x.Scale,y.Scale);
            var exact=multiply?x.Value*y.Value:x.Value*BigInteger.Pow(10,scale-x.Scale)+y.Value*BigInteger.Pow(10,scale-y.Scale);
            int common=Math.Max(scale,z.Scale);
            return exact*BigInteger.Pow(10,common-scale)==z.Value*BigInteger.Pow(10,common-z.Scale);
        }
        catch(OverflowException){return false;}
    }
    private static bool OwnedRecoveryBill(NavalBrokerRawBill b,NavalBrokerRawFill f)
    {
        if(b.Id!=f.BillId||b.Type!="2"||b.Instrument!=f.Instrument||b.OrderId!=f.OrderId||b.TradeId!=f.TradeId||(!string.IsNullOrEmpty(b.ClientId)&&b.ClientId!=f.ClientId)||b.Currency!=f.FeeCurrency||b.SignedFee!=f.SignedFee||b.SourceUtc==null||b.BalanceChange==null||f.Quantity is not >0||f.Price is not >0||f.SignedFee is not <=0)return false;
        try
        {
            decimal change=f.FeeCurrency==f.ActualQuote?checked((f.Side=="sell"?1:-1)*f.Quantity.Value*f.Price.Value+f.SignedFee.Value):checked((f.Side=="buy"?1:-1)*f.Quantity.Value+f.SignedFee.Value);
            // Retain the existing exact-client fee-component form. An absent
            // client is resolved ONLY by immutable bill/fill/order/trade IDs,
            // exact signed net movement and the full current account arithmetic.
            return b.BalanceChange==change||b.ClientId==f.ClientId&&f.Side=="buy"&&f.FeeCurrency!=f.ActualQuote&&b.BalanceChange==f.SignedFee;
        }
        catch(OverflowException){return false;}
    }
}
