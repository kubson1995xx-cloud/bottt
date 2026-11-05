using System.Globalization;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

internal sealed partial class FiniteStopRecovery
{
    private NavalStopOwnedReduction? ownedCap;
    private string? ownedCurrent;
    private OkxPreparedRequest? ownedRequest;
    private NavalBrokerProtection? ownedProtection;
    private NavalBrokerQuote? ownedQuote,ownedUsd;
    private OkxExecutionInstrumentDto? ownedSpec;
    private OkxDemoPriceBand? ownedBand;
    private DateTimeOffset ownedSpecUtc,ownedFeeUtc,ownedCaptureUtc;
    private long ownedSpecMono,ownedFeeMono,ownedCaptureMono;
    private bool ownedClosed;
    private readonly List<object> ownedDiagnostics=new();
    private IReadOnlyList<string> ownedProblems=Array.Empty<string>();
    private static string Number(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
    private bool Recent(DateTimeOffset at,long mono,int seconds)=>UtcNow>=at&&Timestamp>=mono&&UtcNow-at<TimeSpan.FromSeconds(seconds)&&(decimal)(Timestamp-mono)/TimestampFrequency<seconds;
    private bool FreshOwnedQuote(NavalBrokerQuote? q)=>q!=null&&q.Bid>0&&q.Ask>=q.Bid&&q.SourceUtc<=q.ReceivedUtc&&q.ReceivedUtc<=UtcNow&&UtcNow-q.SourceUtc<TimeSpan.FromSeconds(5)&&Timestamp>=q.ReceivedTimestamp&&(decimal)(Timestamp-q.ReceivedTimestamp)/TimestampFrequency<5;
    public bool CanReduce(NavalBrokerIntent original,NavalBrokerIntent mutation,DateTimeOffset now)
    {
        if(!SourceCurrent||ownedCap==null||!ownedClosed||!Recent(ownedCaptureUtc,ownedCaptureMono,30)||ownedRequest==null||ownedRequest.Digest!=mutation.RequestDigest||ownedRequest.Deadline!=mutation.Deadline||ownedRequest.Scope!=scope||original.Id!=ownedCap.ParentId||original.ReservationId!=mutation.ReservationId)return false;
        if(mutation.Operation==NavalExecutionOperation.CancelProtection)return ownedProtection is {State:"live",ChildOrderIds.Count:0}&&ownedProtection.AlgoId==mutation.OrderId&&ownedProtection.AttachmentClientId==mutation.ClientId;
        if(!mutation.Reduction||mutation.Operation!=NavalExecutionOperation.Submit||ownedSpec==null||!Recent(ownedSpecUtc,ownedSpecMono,30)||!Recent(ownedFeeUtc,ownedFeeMono,30)||!FreshOwnedQuote(ownedQuote)||!FreshOwnedQuote(ownedUsd)||ownedBand==null||UtcNow<ownedBand.SourceUtc||UtcNow-ownedBand.SourceUtc>=TimeSpan.FromSeconds(5))return false;
        try
        {
            using var doc=JsonDocument.Parse(mutation.Body);var row=doc.RootElement;
            decimal size=decimal.Parse(row.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture),price=decimal.Parse(row.GetProperty("px").GetString()!,CultureInfo.InvariantCulture);
            bool Cap(string field,string unit,decimal amount)=>ownedSpec.MaxValues.TryGetValue(field,out var m)&&m.Unit==unit&&m.Amount.Value is >0&&amount<=m.Amount.Value;
            decimal usd=checked(size*Math.Max(ownedUsd!.Ask,checked(price/ownedQuote!.Bid*ownedUsd.Ask)));
            return row.GetProperty("side").GetString()=="sell"&&row.GetProperty("tdMode").GetString()=="cash"&&row.GetProperty("ordType").GetString()=="fok"&&!row.TryGetProperty("attachAlgoOrds",out _)&&
                price==decimal.Floor(ownedQuote!.Bid/ownedSpec.TickSize.Value!.Value)*ownedSpec.TickSize.Value.Value&&checked(size*price)<=10&&(!ownedBand.Enabled||ownedBand.SellMinimum is >0&&price>=ownedBand.SellMinimum)&&Cap("maxLmtSz","base currency",size)&&Cap("maxLmtAmt","USD",usd);
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or FormatException or OverflowException){return false;}
    }
    private async Task<string> ReduceOwned(OkxExecutionObservationClient reads,NavalBrokerIntent parent,string fingerprint,FiniteEntryTestComposition? test,
        Func<OkxExecutionClient> writer,Action<string,object> publish,CancellationToken ct)
    {
        using var feed=new OkxPublicMarketFeed(clock,test?.Market);
        string Refusal(string reason){publish("OWNED_REDUCTION_REFUSED_STOP",new{reason,financialClearance=false,problems=ownedProblems,diagnostics=ownedDiagnostics,protectionGapPossible=session.ReadState().NavalBroker!.Operational!.Obligations.Any(o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal))});return reason;}
        try
        {
            if(!await CollectOwned(reads,feed,parent,fingerprint,publish,ct).ConfigureAwait(false))return Refusal("OWNED_RECOVERY_INITIAL_COVERAGE_UNPROVEN_NO_SELL");
            var requested=session.RequestOwnedExit(ownedCap!);if(requested.Status!=NavalMutationStatus.Applied)return Refusal(requested.Code);
            if(ownedProtection?.State=="live")
            {
                var cancel=OkxPreparedRequest.CancelProtection(scope,parent.Instrument,ownedProtection.AlgoId,ownedProtection.AttachmentClientId,OwnedDeadline());
                var result=await DispatchOwned("owned-cancel-"+parent.Id,parent.ReservationId,cancel,false,writer,publish,ct).ConfigureAwait(false);
                if(result.Status!=NavalMutationStatus.Applied)
                {await CollectOwned(reads,feed,parent,fingerprint,publish,ct).ConfigureAwait(false);return Refusal("OWNED_RECOVERY_CANCEL_UNCERTAIN_OR_DENIED_NO_SELL");}
            }
            // ACK is transport only. Recollect terminal protection/children, individual
            // fills, current funding and exact balances before even reserving a sell ID.
            if(!await CollectOwned(reads,feed,parent,fingerprint,publish,ct).ConfigureAwait(false)||ownedProtection==null||!NavalBrokerProtectionOwnership.Settled(ownedProtection))return Refusal("OWNED_RECOVERY_PROTECTION_TERMINAL_OR_ACCOUNTING_UNPROVEN_NO_SELL");
            await OwnedPrivateLimits(reads,parent.Instrument,ct).ConfigureAwait(false);
            var state=session.ReadState();var reservation=state.Naval!.Reservations.Single(r=>r.Id==parent.ReservationId);var spec=NavalBrokerValidation.Definition(state.NavalBroker!.Baseline).Catalog.Single();var fees=state.NavalBroker.Baseline.Observations.Fees;
            decimal size=decimal.Floor(reservation.Inventory/(fees.ExitInBase?1+fees.ExitRate:1)/spec.Quantum)*spec.Quantum;
            if(size<spec.MinimumQuantity)return Refusal("OWNED_RECOVERY_OWN_DUST_NO_SELL");
            ownedUsd=await feed.ReadUsdIndexAsync(spec,ct).ConfigureAwait(false);ownedBand=await feed.ReadPriceBandAsync(spec,ct).ConfigureAwait(false);
            decimal price=decimal.Floor(ownedQuote!.Bid/spec.TickSize)*spec.TickSize;
            var ids=session.ReserveOwnedSellId(ownedCap!);if(ids.Result.Status!=NavalMutationStatus.Applied)return Refusal(ids.Result.Code);
            var sell=OkxPreparedRequest.Submit(scope,spec.Instrument,"sell","fok",Number(size),Number(price),ids.Ids.Single().ClientId,OwnedDeadline());
            var sent=await DispatchOwned("owned-exit-"+parent.Id,parent.ReservationId,sell,true,writer,publish,ct).ConfigureAwait(false);
            // Observe even an uncertain sell. Never retry/reconstruct its ACK.
            bool covered=await CollectOwned(reads,feed,parent,fingerprint,publish,ct).ConfigureAwait(false);state=session.ReadState();var x=state.NavalBroker!.Operational!;reservation=state.Naval!.Reservations.Single();
            bool settled=x.SettledIntents.Contains("owned-exit-"+parent.Id)&&!reservation.ReductionUnsettled&&reservation.TerminalCoverage&&reservation.Inventory>=0&&decimal.Floor(reservation.Inventory/spec.Quantum)*spec.Quantum<spec.MinimumQuantity;
            if(!covered||!settled)return Refusal("OWNED_RECOVERY_SELL_NOT_ECONOMICALLY_FINAL_NO_RETRY_PROTECTION_GAP");
            publish("OWNED_SELL_FILL_FEE_BALANCE_DUST_RECONCILED_STOP",new{sent.Code,residualInventory=reservation.Inventory,protectedResidual=false,originalAckReconstructed=false,newRiskAuthority=false});
            return reservation.Inventory==0?"OWNED_RECOVERY_COMPLETED_STOP_FLAT":"OWNED_RECOVERY_COMPLETED_STOP_WITH_EXACT_UNPROTECTED_DUST";
        }
        catch(OperationCanceledException){return Refusal("OWNED_RECOVERY_CANCELLED_NO_RETRY_CHECK_PROTECTION_GAP");}
        catch(Exception e) when(e is IOException or InvalidDataException or System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException or JsonException or OverflowException)
        {return Refusal("OWNED_RECOVERY_CURRENT_OPERAND_UNPROVEN_NO_SELL_OR_RETRY_CHECK_PROTECTION_GAP");}
        finally{ownedCurrent=null;ownedRequest=null;ownedClosed=false;}
    }
    private DateTimeOffset OwnedDeadline()=>DateTimeOffset.FromUnixTimeMilliseconds(UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
    private async Task<NavalRiskResult> DispatchOwned(string id,string reservation,OkxPreparedRequest request,bool reduction,Func<OkxExecutionClient> writer,Action<string,object> publish,CancellationToken ct)
    {
        var state=session.ReadState();var x=state.NavalBroker!.Operational!;
        ownedRequest=request;
        var intent=new NavalBrokerIntent(id,reservation,Enum.Parse<NavalExecutionOperation>(request.Operation.ToString()),request.Instrument,request.OriginalClientId,request.OrderId,request.CopyBody(),request.Digest,request.Deadline,SessionId,state.Naval!.Generation,state.Naval.Sequence,x.StopEpoch,state.DurableRevision+1,NavalExecutionStage.Prepared){Reduction=reduction};
        var result=session.PrepareOwnedReduction(ownedCap!,intent);if(result.Status!=NavalMutationStatus.Applied){if(ownedDiagnostics.Count<32)ownedDiagnostics.Add(new{reason="OWNED_MUTATION_PREPARE_REFUSED",result.Code});return result;}
        publish("OWNED_MUTATION_DURABLY_PREPARED",new{id,operation=request.Operation.ToString(),reduction});ownedCurrent=id;
        try
        {
            var c=writer();var outcome=request.Operation==OkxExecutionOperation.CancelProtection?await c.CancelProtectionAsync(request,ct).ConfigureAwait(false):await c.SubmitAsync(request,ct).ConfigureAwait(false);
            var disposition=outcome.Disposition==OkxWriteDisposition.Acknowledged?NavalDispatchDisposition.Acknowledged:outcome.Disposition==OkxWriteDisposition.ProvenNotSent?NavalDispatchDisposition.NotSent:NavalDispatchDisposition.Unknown;
            result=session.Outcome(id,new(disposition,outcome.OriginalClientId,outcome.OrderId,disposition==NavalDispatchDisposition.Acknowledged?"TRANSPORT_ACK":disposition==NavalDispatchDisposition.NotSent?"TRANSPORT_NOT_SENT":"TRANSPORT_UNKNOWN",outcome.Code,outcome.SCode,outcome.SubCode));
            publish("OWNED_MUTATION_OUTCOME_NOT_TERMINAL_PROOF",new{id,disposition=disposition.ToString(),result.Code});return result;
        }
        finally{ownedCurrent=null;ownedRequest=null;}
    }
    private async Task OwnedPrivateLimits(OkxExecutionObservationClient reads,string instrument,CancellationToken ct)
    {
        var state=session.ReadState();var baseline=state.NavalBroker!.Baseline;var expected=baseline.Observations.Catalog.Single();
        var r=await reads.ReadInstrumentsAsync(context,ct).ConfigureAwait(false);var spec=r.Status==OkxObservationStatus.Observed?r.Rows.SingleOrDefault(v=>v.Instrument==instrument):null;
        if(spec==null||spec.InstrumentType!="SPOT"||spec.State!="live"||spec.BaseCurrency!=expected.BaseCurrency||spec.CatalogQuote!=expected.QuoteCurrency||!spec.TradeQuoteCurrencies.Contains(expected.QuoteCurrency)||spec.TickSize.Value!=expected.TickSize||spec.LotSize.Value!=expected.Quantum||spec.MinimumSize.Value!=expected.MinimumQuantity)throw new InvalidOperationException("OWNED_SPEC_CHANGED");
        ownedSpec=spec;ownedSpecUtc=UtcNow;ownedSpecMono=Timestamp;
        var fee=await reads.ReadFeesAsync(instrument,context,ct).ConfigureAwait(false);
        if(fee.Status!=OkxObservationStatus.Observed||fee.Rows.Count!=1||spec.GroupId==null||!fee.Rows[0].Groups.TryGetValue(spec.GroupId,out var group)||group.Taker.Value!=-baseline.Observations.Fees.ExitRate||group.Maker.Value is not <=0||fee.Rows[0].Timestamp.Utc is not {} at||at>UtcNow||UtcNow-at>=TimeSpan.FromSeconds(30))throw new InvalidOperationException("OWNED_FEE_CHANGED");
        ownedFeeUtc=at;ownedFeeMono=Timestamp;
    }
    internal static IReadOnlyList<string> PendingOwnedProtectionProblems(OkxExecutionAlgoDto exact,OkxExecutionAlgoDto pending)
        =>OkxPendingProtectionProof.Problems(exact,pending);
    private async Task<bool> CollectOwned(OkxExecutionObservationClient reads,OkxPublicMarketFeed feed,NavalBrokerIntent parent,string fingerprint,Action<string,object> publish,CancellationToken ct)
    {
        ownedClosed=false;var state=session.ReadState();var baseline=state.NavalBroker!.Baseline;var x=state.NavalBroker.Operational!;var r=state.Naval!.Reservations.Single();
        context=new(SessionId,Guid.NewGuid());var capture=UtcNow;long mono=Timestamp;var evidence=new List<NavalBrokerSurface>();var problems=new List<string>();var orders=new List<OkxExecutionOrderDto>();var protection=new List<NavalBrokerProtection>();OkxExecutionAlgoDto? exactProtection=null;
        void Add<T>(OkxObservationRead<T> v){if(v.ResponseDigest!=null)evidence.Add(new(v.Endpoint,v.ExactQuery,v.ResponseDigest,v.Rows.Count,v.RequestUtc,v.ReceiveUtc,v.RequestMonotonic,v.ReceiveMonotonic));}
        void Failed<T>(OkxObservationRead<T> v,string reason){problems.Add(reason);var diagnostic=new{reason,v.Diagnostic};if(ownedDiagnostics.Count<32)ownedDiagnostics.Add(diagnostic);publish("OWNED_OBSERVATION_REFUSED",diagnostic);}
        var account=await reads.ReadAccountAsync(context,ct).ConfigureAwait(false);Add(account);var old=baseline.Observations.Account;
        if(account.Status!=OkxObservationStatus.Observed||account.Rows.Count!=1)Failed(account,"OWNED_ACCOUNT_UNKNOWN");
        else{var a=account.Rows[0];if(a.Uid!=scope.Uid||a.MainUid!=scope.MainUid||a.AccountLevel!=old.Level||a.AccountType!=old.Type||a.SpotRoleType!=old.SpotRole||a.Permissions!=old.Permissions||a.Ip!=old.Ip||a.FeeType!=old.FeeType||a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay)problems.Add("OWNED_ACCOUNT_CHANGED");}
        foreach(var i in x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null))
        {
            var exact=await reads.ReadOwnedRecoveryOrderAsync(new(i.Instrument,i.Outcome?.OrderId,i.ClientId),context,ct).ConfigureAwait(false);Add(exact);
            if(exact.Status!=OkxObservationStatus.Observed||exact.Rows.Count!=1){Failed(exact,"OWNED_EXACT_ORDER_UNKNOWN:"+i.Id);continue;}
            var o=exact.Rows[0];using var body=JsonDocument.Parse(i.Body);var request=body.RootElement;
            if(o.InstType!="SPOT"||o.InstId!=i.Instrument||o.ClOrdId!=i.ClientId||i.Outcome?.OrderId!=null&&o.OrdId!=i.Outcome.OrderId||o.Side!=(i.Reduction?"sell":"buy")||o.TdMode!="cash"||o.TradeQuoteCcy!=scope.QuoteCurrency||o.OrdType!=request.GetProperty("ordType").GetString()||o.Sz.Value!=decimal.Parse(request.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture)||o.State is not ("filled" or "canceled"))
            {problems.Add("OWNED_ORDER_IDENTITY_OR_TERMINAL_CONFLICT:"+i.Id);continue;}
            orders.Add(o);
        }
        // Read the independently allocated protection even if the parent DTO refused.
        var allocated=x.IdAllocations.SelectMany(a=>a.Ids).Where(i=>i.Role==NavalClientIdRole.ProtectionAlgo&&i.ReservationId==parent.ReservationId).ToArray();
        if(allocated.Length!=1)problems.Add("OWNED_PROTECTION_ALLOCATION_UNPROVEN");
        else
        {
            var detail=await reads.ReadOwnedRecoveryAlgoAsync(new(null,allocated[0].ClientId),context,ct).ConfigureAwait(false);Add(detail);
            if(detail.Status!=OkxObservationStatus.Observed||detail.Rows.Count!=1)Failed(detail,"OWNED_EXACT_PROTECTION_UNKNOWN");
            else
            {
                var p=detail.Rows[0];var original=orders.SingleOrDefault(o=>o.ClOrdId==parent.ClientId);var attachments=original?.AttachAlgoOrds;var a=attachments?.Length==1?attachments.Value[0]:null;
                var spec=baseline.Observations.Catalog.Single(v=>v.Instrument==parent.Instrument);
                decimal? net=original?.AccFillSz.Value+(baseline.Observations.Fees.EntryInBase?original?.Fee.Value:0);
                decimal? lot=net is >0?decimal.Floor(net.Value/spec.Quantum)*spec.Quantum:null;
                // Cancellation only. Preserve full-net support; admit only the entire
                // executable own lot plus sub-quantum dust, never arbitrary undersizing.
                bool quantity=p.Sz.Value==net||original?.State=="filled"&&original.AccFillSz.Value==original.Sz.Value&&
                    net is >0&&lot is >0&&lot>=spec.MinimumQuantity&&p.Sz.Value==lot&&net-lot>=0&&net-lot<spec.Quantum;
                bool supported=original!=null&&a!=null&&p.InstType=="SPOT"&&p.InstId==parent.Instrument&&p.AlgoClOrdId==allocated[0].ClientId&&p.Side=="sell"&&p.TdMode=="cash"&&p.TradeQuoteCcy==scope.QuoteCurrency&&p.OrdType is "oco" or "conditional"&&
                    p.State is "live" or "canceled"&&p.TpOrdPx.Value==-1&&p.SlOrdPx.Value==-1&&p.TpTriggerPxType=="last"&&p.SlTriggerPxType=="last"&&quantity&&
                    p.SlTriggerPx.Value==r.Intent.StopPrice&&p.TpTriggerPx.Value==r.Intent.TakeProfitPrice&&p.FailCode is null or "" or "0"&&p.SubAlgoIdList is not {Length:>0}&&p.AdvChaseParams is not {Length:>0}&&p.AttachAlgoOrds is not {Length:>0}&&
                    string.IsNullOrEmpty(p.OrdId)&&string.IsNullOrEmpty(p.LinkedOrd?.OrdId)&&p.ActualSz.Value is null or 0&&p.ActualPx.Value is null or 0&&string.IsNullOrEmpty(p.ActualSide)&&string.IsNullOrEmpty(p.AdvanceOrdType)&&p.IsTradeBorrowMode is null or "" or "false"&&
                    a.AttachAlgoClOrdId==allocated[0].ClientId&&!string.IsNullOrEmpty(a.AttachAlgoId)&&p.AlgoId!=a.AttachAlgoId&&a.FailCode is null or "" or "0"&&
                    !x.Protection.Any(v=>v.AttachmentClientId==allocated[0].ClientId&&v.AttachmentId!=null&&(v.Instrument!=p.InstId||v.ParentOrderId!=original.OrdId||v.AttachmentId!=a.AttachAlgoId||v.AlgoId!=p.AlgoId));
                if(!supported||p.OrdIdList is not {Length:0})problems.Add(p.OrdIdList is {Length:>0}?"OWNED_PROTECTION_CHILD_UNSUPPORTED_NO_SELL":!p.OrdIdList.HasValue?"OWNED_PROTECTION_CHILD_RELATION_UNPROVEN_NO_SELL":!quantity?"OWNED_PROTECTION_QUANTITY_UNSUPPORTED_NO_SELL":"OWNED_PROTECTION_TYPE_CONFIG_OR_IDENTITY_UNSUPPORTED_NO_SELL");
                else if(original!=null&&a!=null){exactProtection=p;protection.Add(new(p.InstId,original.OrdId,allocated[0].ClientId,p.AlgoId,p.State,p.Sz.Value,p.SlTriggerPx.Value,p.TpTriggerPx.Value,Array.Empty<string>(),p.PayloadDigest){AttachmentId=a.AttachAlgoId});}
            }
        }
        var fills=new List<OkxExecutionFillDto>();var bills=new List<OkxExecutionBillDto>();var pager=new OkxObservationPager(reads);
        var begin=DateTimeOffset.FromUnixTimeMilliseconds(baseline.Observations.CaptureUtc.ToUnixTimeMilliseconds());var end=DateTimeOffset.FromUnixTimeMilliseconds(UtcNow.ToUnixTimeMilliseconds());if(end<=begin)end=begin.AddMilliseconds(1);
        foreach(var surface in new[]{OkxObservationSurface.Pending,OkxObservationSurface.Fills,OkxObservationSurface.FillsArchive,OkxObservationSurface.Bills,OkxObservationSurface.BillsArchive})
        {
            var collection=await pager.CollectAsync(new(surface,surface==OkxObservationSurface.Pending?new(null,null,null,null,null):new(null,null,begin,end,null),context),ct).ConfigureAwait(false);
            if(collection.Status!=OkxObservationCollectionStatus.Collected)problems.Add("OWNED_HISTORY_INCOMPLETE:"+surface);
            if(collection.Orders.Count>0)problems.Add("OWNED_PENDING_OR_FOREIGN_ORDER_NO_SELL");fills.AddRange(collection.Fills);bills.AddRange(collection.Bills);
            foreach(var p in collection.Trace)if(p.ResponseDigest!=null)evidence.Add(new(p.Endpoint,p.ExactQuery,p.ResponseDigest,p.RowCount,p.RequestUtc,p.ReceiveUtc,p.RequestMonotonic,p.ReceiveMonotonic));
        }
        foreach(var filter in new[]{OkxAlgoOrderFilter.ConditionalAndOco,OkxAlgoOrderFilter.Trigger,OkxAlgoOrderFilter.TrailingStop})
        {
            var pending=await reads.ReadPendingAlgosAsync(new(OkxAlgoObservationSurface.Pending,filter,null,null,null,null),context,ct).ConfigureAwait(false);Add(pending);
            if(pending.Status!=OkxObservationStatus.Observed||pending.Rows.Count>=100)problems.Add("OWNED_PENDING_ALGOS_INCOMPLETE");
            else foreach(var p in pending.Rows)
            {
                if(exactProtection==null||p.InstId!=exactProtection.InstId||p.AlgoId!=exactProtection.AlgoId||p.AlgoClOrdId!=exactProtection.AlgoClOrdId)
                {problems.Add("OWNED_FOREIGN_OR_UNLINKED_PENDING_ALGO_NO_SELL");continue;}
                var conflicts=PendingOwnedProtectionProblems(exactProtection,p);if(conflicts.Count==0)continue;
                problems.AddRange(conflicts.Select(reason=>reason+":"+p.PayloadDigest));
                var diagnostic=new{reasons=conflicts,exactDigest=exactProtection.PayloadDigest,pendingDigest=p.PayloadDigest,exactState=exactProtection.State,pendingState=p.State,childOrderIds=p.OrdIdList,subAlgoIds=p.SubAlgoIdList,childRelationKnown=p.OrdIdList.HasValue};
                if(ownedDiagnostics.Count<32)ownedDiagnostics.Add(diagnostic);publish("OWNED_PENDING_PROTECTION_CONFLICT_NO_SELL",diagnostic);
            }
        }
        var balance=await reads.ReadBalancesAsync(context,ct).ConfigureAwait(false);Add(balance);var balances=balance.Status==OkxObservationStatus.Observed&&balance.Rows.Count==1?balance.Rows[0].Details.Select(OkxBrokerObservationMapper.MapBalance).ToArray():Array.Empty<NavalBrokerBalance>();if(balances.Length==0)problems.Add("OWNED_BALANCE_UNKNOWN");
        CurrentMarketFundingEvidenceV1? funding=null;
        if(baseline.Observations.CurrentMarketFunding!=null)
        {try{funding=await OkxCurrentMarketFunding.ReadOwnedRecoveryAsync(reads,context,baseline.Observations.OperatorPremise!,fingerprint,balances,clock,ct).ConfigureAwait(false);evidence.Add(funding.Response);}catch(InvalidOperationException){problems.Add("OWNED_CURRENT_MARKET_FUNDING_UNPROVEN");}}
        var quotes=new Dictionary<string,NavalBrokerQuote>();try{ownedQuote=await feed.ReadQuoteAsync(baseline.Observations.Catalog.Single(),ct).ConfigureAwait(false);quotes.Add(parent.Instrument,ownedQuote);}catch(Exception e) when(e is IOException or InvalidDataException or System.Net.Http.HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException){problems.Add("OWNED_CURRENT_QUOTE_UNPROVEN");}
        var raw=fills.GroupBy(f=>(f.InstId,f.TradeId,f.PayloadDigest)).Select(g=>g.First()).Select(f=>new NavalBrokerRawFill(f.InstId,f.OrdId,f.ClOrdId,f.TradeId,f.BillId,f.Side??"",f.FillSz.Value,f.FillPx.Value,f.Fee.Value,f.FeeCcy,f.TradeQuoteCcy,f.FillTime.Utc,f.PayloadDigest)).ToArray();
        foreach(var o in orders)
        {
            var trades=raw.Where(f=>f.Instrument==o.InstId&&f.OrderId==o.OrdId&&f.ClientId==o.ClOrdId).GroupBy(f=>f.TradeId).Select(g=>g.First()).ToArray();
            if(o.State=="filled"&&(trades.Length==0||trades.Any(f=>f.Quantity is not >0||f.Price is not >0)||o.AccFillSz.Value!=trades.Sum(f=>f.Quantity)||o.AvgPx.Value*o.AccFillSz.Value!=trades.Sum(f=>f.Quantity*f.Price)))problems.Add("OWNED_ORDER_AVERAGE_INDIVIDUAL_FILL_CONFLICT");
        }
        var billRows=bills.GroupBy(v=>(v.BillId,v.PayloadDigest)).Select(g=>g.First()).Select(v=>new NavalBrokerRawBill(v.BillId,v.InstId,v.OrdId,v.ClOrdId,v.TradeId,v.Type,v.Ccy,v.Fee.Value,v.BalChg.Value,v.Ts.Utc,v.PayloadDigest)).ToArray();
        var proofs=orders.Select(o=>{var a=o.AttachAlgoOrds?.FirstOrDefault();var p=protection.SingleOrDefault(v=>v.ParentOrderId==o.OrdId);return new NavalBrokerOrderProof(o.InstId,o.OrdId,o.ClOrdId,o.State,o.Sz.Value,o.AccFillSz.Value,o.Fee.Value,o.FeeCcy,o.TradeQuoteCcy,a?.AttachAlgoClOrdId,p?.AlgoId,o.PayloadDigest){AttachmentId=a?.AttachAlgoId};}).ToArray();
        var result=session.ApplyOwnedEconomics(ownedCap!,new(capture,mono,problems.Count==0,raw,billRows,proofs,protection,balances,quotes,evidence,problems){CurrentMarketFunding=funding,OperatorPremise=baseline.Observations.OperatorPremise,ObservationCapture=context.CaptureId});
        state=session.ReadState();ownedProblems=problems.ToArray();ownedProtection=protection.SingleOrDefault();ownedCaptureUtc=capture;ownedCaptureMono=mono;ownedClosed=problems.Count==0&&state.Naval!.Reservations.Single().TerminalCoverage&&!state.Naval.Reservations.Single().ReductionUnsettled&&state.NavalBroker!.Operational!.Obligations.All(o=>o=="PROTECTION_GAP:"+parent.ReservationId)&&result.Code is not ("STORAGE_FROZEN" or "COMMIT_CONFLICT");
        publish("OWNED_RETROSPECTIVE_ACCOUNTING_STOP",new{result.Code,closed=ownedClosed,problems,obligations=state.NavalBroker!.Operational!.Obligations,exactParentObserved=orders.Any(o=>o.ClOrdId==parent.ClientId),exactProtectionObserved=protection.Count==1,newRiskAuthority=false});return ownedClosed;
    }
}
