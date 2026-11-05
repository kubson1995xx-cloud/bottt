using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Only release-coded refusal identifiers may leave this boundary. Operands remain private.
internal sealed class OkxFiniteAccountRefusal(string code,object? operands=null):InvalidOperationException(code)
{
    internal string Code { get; }=code;
    internal object? Operands { get; }=operands;
}
internal sealed record OkxFiniteUnavailableBook(string Instrument,string Reason,int AskRows,int BidRows,DateTimeOffset SourceUtc);
internal sealed record OkxFiniteNomination(string Quote,decimal Cash,NavalInstrument Instrument,NavalFeeSchedule Fees,OkxFiniteOrderPlan Plan,string Reason)
{
    public IReadOnlyList<OkxFiniteUnavailableBook> UnavailableBooks { get; init; }=Array.Empty<OkxFiniteUnavailableBook>();
}
internal sealed record OkxFiniteSelectionDiagnostic(string Stage,string Reason,OkxPublicFeedDiagnostic? PublicFeed);
internal static class OkxFiniteAccountSelection
{
    // Read-only request-local preflight, structurally no execution client/owner/START.
    internal static async Task<OkxFiniteNomination> ObserveAsync(OkxExecutionScope scope,IOkxSigningMaterialProvider secrets,string fingerprint,Func<bool> safe,CancellationToken ct,
        Action<object>? diagnostics=null,TimeProvider? time=null,Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient>? factory=null,System.Net.Http.HttpMessageHandler? market=null)
        =>await ObserveFundedCoreAsync(scope,secrets,fingerprint,safe,ct,diagnostics,time,factory,market,null,null).ConfigureAwait(false);
    internal static Task<OkxFiniteNomination> ObserveFundedAsync(OkxExecutionScope scope,IOkxSigningMaterialProvider secrets,string fingerprint,Func<bool> safe,CancellationToken ct,NavalFiniteOperatorPremise premise,
        Action<object>? diagnostics=null,TimeProvider? time=null,Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient>? factory=null,System.Net.Http.HttpMessageHandler? market=null,Action<OkxFiniteSelectionDiagnostic>? boundary=null)
        =>ObserveFundedCoreAsync(scope,secrets,fingerprint,safe,ct,diagnostics,time,factory,market,premise,boundary);
    private static async Task<OkxFiniteNomination> ObserveFundedCoreAsync(OkxExecutionScope scope,IOkxSigningMaterialProvider secrets,string fingerprint,Func<bool> safe,CancellationToken ct,
        Action<object>? diagnostics,TimeProvider? time,Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient>? factory,System.Net.Http.HttpMessageHandler? market,NavalFiniteOperatorPremise? premise,Action<OkxFiniteSelectionDiagnostic>? boundary)
    {
        if(scope.Environment!="Demo"||scope.Uid!=scope.MainUid)throw new OkxFiniteAccountRefusal("FINITE_DEMO_MAIN_ACCOUNT_REQUIRED");
        var clock=time??TimeProvider.System;var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(90));
        var fence=new SelectionFence(scope,context,safe,clock,budget.Token);
        await using var reads=(factory??OkxExecutionObservationClient.Create)(scope,secrets,fingerprint,fence);
        using var feed=new OkxPublicMarketFeed(clock,market);
        T One<T>(OkxObservationRead<T> r)
        {if(r.Status!=OkxObservationStatus.Observed||r.Rows.Count!=1)throw new OkxFiniteAccountRefusal("FINITE_PREFLIGHT_READ_UNPROVEN");return r.Rows[0];}
        string stage="ACCOUNT_CONFIG",reason="COMPLETED";
        try
        {
            var a=One(await reads.ReadAccountAsync(context,budget.Token).ConfigureAwait(false));
            if(a.Uid!=scope.Uid||a.MainUid!=scope.MainUid||a.Permissions!="read_only,trade"||a.AccountLevel!="1"||a.AccountType!="0"||a.SpotRoleType!="0"||
                a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay||string.IsNullOrWhiteSpace(a.Ip))
            {
                // Project every failed predicate, never the private provider values.
                // This is diagnostics only: the exact admission expression above is unchanged.
                var reasons=new List<string>();
                if(a.Uid!=scope.Uid)reasons.Add("UID_SCOPE_MISMATCH");
                if(a.MainUid!=scope.MainUid)reasons.Add("MAIN_UID_SCOPE_MISMATCH");
                if(a.Permissions!="read_only,trade")reasons.Add("PERMISSION_NOT_EXACT_READ_AND_TRADE");
                if(a.AccountLevel!="1")reasons.Add("ACCOUNT_LEVEL_NOT_1");
                if(a.AccountType!="0")reasons.Add("ACCOUNT_TYPE_NOT_0");
                if(a.SpotRoleType!="0")reasons.Add("SPOT_ROLE_TYPE_NOT_0");
                if(a.AutoLoan)reasons.Add("AUTO_LOAN_ENABLED");
                if(a.EnableSpotBorrow)reasons.Add("SPOT_BORROW_ENABLED");
                if(a.SpotBorrowAutoRepay)reasons.Add("SPOT_BORROW_AUTO_REPAY_ENABLED");
                if(string.IsNullOrWhiteSpace(a.Ip))reasons.Add("IP_MISSING");
                throw new OkxFiniteAccountRefusal("FINITE_ACCOUNT_PERMISSION_ROLE_OR_IP_UNSAFE",new{
                    Reasons=reasons.ToArray(),UidMatchesScope=a.Uid==scope.Uid,MainUidMatchesScope=a.MainUid==scope.MainUid,
                    PermissionProfile=a.Permissions switch {"read_only,trade"=>"READ_AND_TRADE_EXACT","trade,read_only"=>"READ_AND_TRADE_REVERSED","read_only"=>"READ_ONLY","read_only,trade,withdraw"=>"READ_TRADE_AND_WITHDRAW",_=>"UNRECOGNIZED"},
                    AccountLevelIs1=a.AccountLevel=="1",AccountTypeIs0=a.AccountType=="0",SpotRoleTypeIs0=a.SpotRoleType=="0",
                    a.AutoLoan,a.EnableSpotBorrow,a.SpotBorrowAutoRepay,IpMissing=string.IsNullOrWhiteSpace(a.Ip)});
            }
            stage="ACCOUNT_BALANCE";var balances=One(await reads.ReadBalancesAsync(context,budget.Token).ConfigureAwait(false));
            var raw=balances.Details.Select(OkxBrokerObservationMapper.MapBalance).ToArray();
            stage="CURRENT_MARKET_FUNDING";var funding=premise==null?null:await OkxCurrentMarketFunding.ReadAsync(reads,context,premise,fingerprint,raw,clock,budget.Token).ConfigureAwait(false);
            stage="ACCOUNT_INSTRUMENTS";var instruments=await reads.ReadInstrumentsAsync(context,budget.Token).ConfigureAwait(false);
            if(instruments.Status!=OkxObservationStatus.Observed)throw new OkxFiniteAccountRefusal("FINITE_CATALOG_UNPROVEN");
            return await SelectCoreAsync(raw,instruments.Rows,a.FeeType,clock,
                async(i,c)=>One(await reads.ReadFeesAsync(i,context,c).ConfigureAwait(false)),feed.ReadFiniteQuoteAsync,budget.Token,funding,s=>stage=s).ConfigureAwait(false);
        }
        catch(Exception e)
        {
            reason=e is OkxFiniteAccountRefusal?"POLICY_REFUSAL":e is OperationCanceledException?"CANCELLED":stage=="PUBLIC_BOOK"&&feed.LastDiagnostic is {} d?d.Reason=="PAYLOAD_OBSERVED"?"PUBLIC_SCHEMA_INVALID":d.Reason:"UNEXPECTED_FAILURE";
            throw; // Diagnostics are not authority, recovery, rejection proof or a new candidate policy.
        }
        finally{boundary?.Invoke(new(stage,reason,feed.LastDiagnostic));diagnostics?.Invoke(reads.ReadDiagnostics());}
    }
    private sealed class SelectionFence(OkxExecutionScope scope,OkxObservationContext context,Func<bool> safe,TimeProvider clock,CancellationToken ct):IOkxObservationReadFence
    {
        private readonly DateTimeOffset start=clock.GetUtcNow();private readonly long mono=clock.GetTimestamp();
        public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset now)=>s==scope&&c==context&&!ct.IsCancellationRequested&&safe()&&
            now>=start&&now-start<TimeSpan.FromSeconds(90)&&clock.GetTimestamp()>=mono&&clock.GetElapsedTime(mono)<TimeSpan.FromSeconds(90);
    }
    internal static async Task<OkxFiniteNomination> SelectAsync(IReadOnlyList<NavalBrokerBalance> balances,IReadOnlyList<OkxExecutionInstrumentDto> instruments,string feeType,TimeProvider clock,
        Func<string,CancellationToken,Task<OkxExecutionFeeDto>> readFees,Func<NavalInstrument,CancellationToken,Task<NavalBrokerQuote>> readQuote,CancellationToken ct)
        =>await SelectCoreAsync(balances,instruments,feeType,clock,readFees,readQuote,ct,null).ConfigureAwait(false);
    private static async Task<OkxFiniteNomination> SelectCoreAsync(IReadOnlyList<NavalBrokerBalance> balances,IReadOnlyList<OkxExecutionInstrumentDto> instruments,string feeType,TimeProvider clock,
        Func<string,CancellationToken,Task<OkxExecutionFeeDto>> readFees,Func<NavalInstrument,CancellationToken,Task<NavalBrokerQuote>> readQuote,CancellationToken ct,CurrentMarketFundingEvidenceV1? funding,Action<string>? stage=null)
    {
        if(feeType is not ("0" or "1"))throw new OkxFiniteAccountRefusal("FINITE_FEE_POLICY_UNSUPPORTED");
        if(balances.Count is <1 or >256||balances.Select(b=>b.Currency).Distinct(StringComparer.Ordinal).Count()!=balances.Count||
            balances.Any(b=>!NavalCurrentMarketFunding.Balance(b,funding!=null)||b.SourceUtc>clock.GetUtcNow()))throw new OkxFiniteAccountRefusal("FINITE_BALANCE_INVALID_OR_ENCUMBERED",balances);
        var attempts=new List<object>();var unavailableBooks=new List<OkxFiniteUnavailableBook>();bool funded=false,unheld=false;
        // Ordered currency policy; never compare/sum amounts or costs across currencies.
        foreach(string quote in new[]{"EUR","USDC"})
        {
            var cash=balances.SingleOrDefault(b=>b.Currency==quote)?.Available;
            if(cash is not >0)continue;funded=true;
            var choices=new List<OkxFiniteNomination>();
            foreach(var i in instruments.Where(i=>NavalFleetProfiles.Get(NavalFleet.Bravo).Assets.Contains(i.BaseCurrency)&&i.CatalogQuote==quote&&i.TradeQuoteCurrencies.Contains(quote)&&
                i.InstrumentType=="SPOT"&&i.State=="live"&&!balances.Any(b=>b.Currency==i.BaseCurrency&&b.Cash!=0)).OrderBy(i=>i.Instrument,StringComparer.Ordinal))
            {
                unheld=true;
                if(i.Instrument!=i.BaseCurrency+"-"+quote||i.GroupId==null||i.LotSize.Value is not >0||i.MinimumSize.Value is not >0||i.TickSize.Value is not >0||i.ListTime.Utc>clock.GetUtcNow()||i.ExpiryTime.Utc!=null)
                {attempts.Add(new{i.Instrument,code="FINITE_SPEC_UNSUPPORTED"});continue;}
                stage?.Invoke("ACCOUNT_FEE");var f=await readFees(i.Instrument,ct).ConfigureAwait(false);
                if(f.InstrumentType!="SPOT"||f.Timestamp.Utc==null||f.Timestamp.Utc>clock.GetUtcNow()||clock.GetUtcNow()-f.Timestamp.Utc>=TimeSpan.FromSeconds(30)||
                    !f.Groups.TryGetValue(i.GroupId,out var g)||g.Taker.Value is not <0||g.Taker.Value<=-1||g.Maker.Value is not <=0||g.Maker.Value<g.Taker.Value)
                {attempts.Add(new{i.Instrument,i.GroupId,code="FINITE_FEE_GROUP_OR_MAKER_BOUND_UNSUPPORTED"});continue;}
                var fees=new NavalFeeSchedule(-g.Taker.Value.Value,feeType=="0",-g.Taker.Value.Value,false);
                var spec=new NavalInstrument(i.Instrument,i.BaseCurrency,quote,i.LotSize.Value.Value,i.MinimumSize.Value.Value,i.TickSize.Value.Value);
                stage?.Invoke("PUBLIC_BOOK");NavalBrokerQuote book;
                try{book=await readQuote(spec,ct).ConfigureAwait(false);}
                catch(OkxBookSideUnavailable e) when(!ct.IsCancellationRequested&&e.IsCurrent(spec.Instrument,clock))
                {
                    // Only confirmed fresh empty-side availability may continue;
                    // uncertainty and every other exception still propagate/STOP.
                    if(unavailableBooks.Count>=256)throw new OkxFiniteAccountRefusal("FINITE_AVAILABILITY_ATTEMPT_LIMIT");
                    unavailableBooks.Add(e.Attempt);attempts.Add(e.Attempt);continue;
                }
                stage?.Invoke("QUOTE_VALIDATION");
                if(book.Instrument!=spec.Instrument||book.SourceUtc>book.ReceivedUtc||book.ReceivedUtc>clock.GetUtcNow()||clock.GetUtcNow()-book.SourceUtc>=TimeSpan.FromSeconds(5)||book.ReceivedTimestamp>clock.GetTimestamp())
                throw new OkxFiniteAccountRefusal("FINITE_QUOTE_STALE",new{spec,book});
                try
                {
                    stage?.Invoke("ROUNDTRIP_SIZING");var plan=OkxOperationalTradingService.MinimumRoundTrip(spec,fees,book,false);
                    var definition=new NavalPortfolioDefinition(new("finite-preflight",quote),new(),Array.Empty<NavalSeed>(),new[]{spec},fees);
                    var holds=NavalCashRiskPolicy.Holds(new NavalReservation{Intent=new("preflight","preflight",spec.Instrument,plan.GrossQuantity,plan.Price,plan.Stop,plan.TakeProfit),Quantity=plan.GrossQuantity,Pending=plan.GrossQuantity},definition,new Dictionary<string,decimal>{{spec.Instrument,book.Bid}});
                    if(NavalCashRiskPolicy.Held(holds)>cash||holds.RiskHold>cash*.005m)
                    {attempts.Add(new{spec,fees,book,plan,cash,holds.EntryHold,holds.CloseFeeHold,holds.RiskHold,code="FINITE_SELECTED_QUOTE_CASH_OR_RISK_INSUFFICIENT"});continue;}
                    choices.Add(new(quote,cash.Value,spec,fees,plan,"EUR before USDC among independently feasible funded quotes; then minimum debit within that quote, then ordinal instrument. Other balances excluded; no conversion."));
                }
                catch(OkxFiniteSizingRefusal e){attempts.Add(new{spec,fees,book,plan=e.Plan,code="FINITE_MINIMUM_ROUNDTRIP_EXCEEDS_CAP_OR_DEPTH"});}
                catch(InvalidOperationException){attempts.Add(new{spec,fees,book,code="FINITE_SPEC_OR_PROTECTION_UNSUPPORTED"});}
            }
            if(choices.Count>0){stage?.Invoke("NOMINATED");return choices.OrderBy(c=>c.Plan.Debit).ThenBy(c=>c.Instrument.Instrument,StringComparer.Ordinal).First() with{UnavailableBooks=unavailableBooks.ToArray()};}
        }
        throw new OkxFiniteAccountRefusal(!funded?"FINITE_NO_FUNDED_EUR_OR_USDC":!unheld?"FINITE_NO_UNHELD_SUPPORTED_BASE":"FINITE_NO_FEASIBLE_ROUNDTRIP_FOR_FUNDED_QUOTES",new{balances,attempts,instruments=instruments.Select(i=>new{i.Instrument,i.BaseCurrency,i.CatalogQuote,i.State}).ToArray()});
    }
}
