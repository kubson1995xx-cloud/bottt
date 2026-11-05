using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Host-callable single-account loop over the accepted account service and SAME durable owner.
// This class is not constructed by default startup. Native/panel wiring is a separate delivery gate.
internal sealed partial class OkxOperationalTradingService : IAsyncDisposable,INavalBrokerTradingAuthority,INavalFiniteDemoAuthority,IOkxSerializedDispatchFence
{
    private readonly OkxAccountConnectionService account;
    private readonly NavalBrokerSession session;
    private readonly TimeProvider clock;
    private readonly OkxPublicMarketFeed feed;
    private readonly OkxExecutionClient client;
    private readonly SemaphoreSlim lane=new(1,1);
    private readonly string? protection;
    private readonly Dictionary<string,NavalBrokerQuote> quotes=new(StringComparer.Ordinal);
    private volatile bool armed,disposed;
    private string? current;
    public Guid SessionId { get; }
    public bool SourceCurrent=>!disposed&&account.DispatchSafe&&account.IsConnected&&ReferenceEquals(account.BrokerSession,session)&&account.TradingSessionId==SessionId;
    private long reconciledActivityVersion=-1;
    public bool CanExecute=>SourceCurrent&&armed&&account.ActivityVersion==Interlocked.Read(ref reconciledActivityVersion);
    public string? ProtectionProfile=>protection;
    public bool FiniteDemo=>account.IsFiniteDemo;
    public bool OrdinaryDemoPolicy=>account.OrdinaryDemoPolicy;
    internal bool ArmFiniteDemo(){armed=account.ArmFiniteDemo();return armed;}
    private bool EntryObservationPending=>session.ReadState().NavalBroker!.Operational is {} x&&x.Intents.Any(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction&&i.Stage!=NavalExecutionStage.NotSent&&!x.SettledIntents.Contains(i.Id));
    internal string Status=>disposed?"DISPOSED":!SourceCurrent?"DISCONNECTED_STOP":protection==null?OkxConditionalCashSpotPolicy.Refusal:!armed?"EXECUTE_ARMING_REQUIRED":session.Coordinator.Snapshot().Mode=="RUNNING"&&EntryObservationPending?"ENTRY_OBSERVATION_PENDING":session.Coordinator.Snapshot().Reason;
    private OkxOperationalTradingService(OkxAccountConnectionService account,TimeProvider clock,Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxFinalDispatchFence,OkxExecutionClient>? writes,HttpMessageHandler? market,string? protection)
    {
        this.account=account;this.clock=clock;this.protection=protection;session=account.BrokerSession??throw new InvalidOperationException("BROKER_ACCOUNT_REQUIRED");SessionId=account.TradingSessionId;
        if(SessionId==Guid.Empty)throw new InvalidOperationException("FRESH_CONNECTION_REQUIRED");
        feed=new(clock,market);var scope=account.ExecutionScope!;var provider=account.ExecutionSigningProvider();var fingerprint=session.ReadState().NavalBroker!.Baseline.Binding.CredentialFingerprint;
        client=(writes??OkxExecutionClient.Create)(scope,provider,fingerprint,this);
        account.RegisterTrading(this);session.BindTrading(this);
    }
    // The optional internal wire/clock seam changes transport only; production policy is
    // always issued by the same release-coded evaluator, never supplied by a fixture.
    internal static OkxOperationalTradingService Create(OkxAccountConnectionService account,TimeProvider? clock=null,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxFinalDispatchFence,OkxExecutionClient>? writes=null,HttpMessageHandler? market=null)
    {
        var baseline=account.BrokerSession?.ReadState().NavalBroker!.Baseline??throw new InvalidOperationException("BROKER_ACCOUNT_REQUIRED");
        return new(account,clock??TimeProvider.System,writes,market,OkxConditionalCashSpotPolicy.Admit(baseline)==null?null:OkxConditionalCashSpotPolicy.Id);
    }
    internal static OkxOperationalTradingService ForTests(OkxAccountConnectionService account,Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxFinalDispatchFence,OkxExecutionClient> writes,HttpMessageHandler market,TimeProvider clock,bool protocolProtection=true)
        =>new(account,clock,writes,market,protocolProtection?"SYNTHETIC-PROTOCOL-ONLY-NOT-PRODUCTION":null);
    private long armingEpoch;
    public long ContinuationEpoch=>protection==OkxConditionalCashSpotPolicy.Id&&!FiniteDemo?Interlocked.Read(ref armingEpoch):-1;
    internal bool Arm(OkxActivationRequest request,OkxActivationLease lease)
    {
        session.RevokeStrategyContinuation();RevokeArming();
        long epoch=Interlocked.Read(ref armingEpoch);
        bool approved=SourceCurrent&&account.ArmExecution(request,lease);
        armed=approved&&epoch==Interlocked.Read(ref armingEpoch);
        // STOP may happen after the equality check but before the volatile assignment.
        if(epoch!=Interlocked.Read(ref armingEpoch))armed=false;
        return armed;
    }
    private void RevokeArming(){Interlocked.Increment(ref armingEpoch);armed=false;}
    private NavalRiskResult Refuse(string code)=>new(NavalMutationStatus.Refused,code,null,session.Coordinator.Snapshot().DurableRevision);
    private static IReadOnlyList<T> Observed<T>(OkxObservationRead<T> read)
    {if(read.Status!=OkxObservationStatus.Observed)throw new InvalidDataException("BROKER_OBSERVATION_FAILED:"+read.ErrorCode);return read.Rows;}
    internal async Task<NavalRiskResult> ReconcileAsync(CancellationToken ct)
    {
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!SourceCurrent){session.RevokeStrategyContinuation();return Refuse("FRESH_CONNECTION_REQUIRED");}
            var reads=account.ObservationClient!;var context=account.ObservationContext!;var state=session.ReadState();var baseline=state.NavalBroker!.Baseline;
            var a=Observed(await reads.ReadAccountAsync(context,ct)).Single();
            if(a.Uid!=baseline.Binding.Account.AccountId||a.MainUid!=baseline.Binding.MainUid||a.AccountLevel!="1"||a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay||a.Permissions!="read_only,trade"||a.FeeType!=baseline.Observations.Account.FeeType||a.Ip!=baseline.Observations.Account.Ip||a.AccountType!="0"||a.SpotRoleType!="0")return session.Coordinator.Stop("BROKER_ACCOUNT_CHANGED");
            var specs=Observed(await reads.ReadInstrumentsAsync(context,ct));
            privateSpecs.Clear();specUtc=clock.GetUtcNow();specTimestamp=clock.GetTimestamp();
            foreach(var i in NavalOrdinaryCatalogPolicy.Catalog(state.NavalBroker!))
            {
                var spec=specs.SingleOrDefault(s=>s.Instrument==i.Instrument);
                if(spec==null||spec.State!="live"||spec.InstrumentType!="SPOT"||spec.BaseCurrency!=i.BaseCurrency||spec.CatalogQuote!=i.QuoteCurrency||!spec.TradeQuoteCurrencies.Contains(i.QuoteCurrency)||
                    spec.LotSize.Value!=i.Quantum||spec.TickSize.Value!=i.TickSize||spec.MinimumSize.Value!=i.MinimumQuantity)return session.Coordinator.Stop("BROKER_SPEC_CHANGED");
                privateSpecs.Add(i.Instrument,spec);
                if(Conditional)
                {
                    var fee=Observed(await reads.ReadFeesAsync(i.Instrument,context,ct)).Single();
                    if(spec.GroupId==null||!fee.Groups.TryGetValue(spec.GroupId,out var group)||group.Taker.Value!=-baseline.Observations.Fees.EntryRate||group.Maker.Value is not <=0||account.IsFiniteDemo&&group.Maker.Value<group.Taker.Value||fee.Timestamp.Utc==null||fee.Timestamp.Utc>clock.GetUtcNow()||clock.GetUtcNow()-fee.Timestamp.Utc>=TimeSpan.FromSeconds(30))return session.Coordinator.Stop("BROKER_FEES_CHANGED");
                }
            }
            var result=await CollectEconomicsAsync(ct).ConfigureAwait(false);
            if(result.Status==NavalMutationStatus.Applied)session.ContinueStrategy();
            else if(result.Status==NavalMutationStatus.Refused)session.RevokeStrategyContinuation();
            return result;
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException)
        {return session.Coordinator.Stop("BROKER_RECONCILIATION_INCOMPLETE");}
        finally{lane.Release();}
    }
    internal NavalRiskResult Start()=>session.StartTrading();
    internal async Task<NavalRiskResult> RunOnceAsync(NavalFleet fleet,string instrument,CancellationToken ct)
    {
        await lane.WaitAsync(ct).ConfigureAwait(false);
        bool evaluated=false;
        try
        {
            if(account.IsFiniteDemo){EvaluationCode("SKIPPED_FINITE");return Refuse("FINITE_DEMO_NO_STRATEGY_SCHEDULER");}
            if(!CanExecute||protection==null){EvaluationCode("SKIPPED_CAN_EXECUTE");return Refuse(protection==null?"PRODUCTION_PROTECTION_PROFILE_UNAPPROVED":"EXECUTE_ARMING_REQUIRED");}
            if(session.Coordinator.Snapshot().Mode!="RUNNING"){EvaluationCode("SKIPPED_NOT_RUNNING");return Refuse("EXPLICIT_START_REQUIRED");}
            if(EntryObservationPending){EvaluationCode("SKIPPED_ENTRY_OBSERVATION");return Refuse("ENTRY_OBSERVATION_PENDING");}
            var state=session.ReadState();var baseline=state.NavalBroker!.Baseline;var definition=NavalOrdinaryCatalogPolicy.Definition(state.NavalBroker);
            var spec=definition.Catalog.SingleOrDefault(i=>i.Instrument==instrument);
            if(spec==null){EvaluationCode("SKIPPED_INELIGIBLE");return Refuse("INSTRUMENT_INELIGIBLE");}
            var series=await feed.ReadSeriesAsync(spec,ct).ConfigureAwait(false);
            var cutoff=new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);var hasLong=state.Naval!.Reservations.Any(r=>r.Fleet==fleet&&r.Intent.Instrument==instrument&&r.Inventory>0&&r.QuarantineMark==null);
            NavalSeries? btc=null;
            if(!hasLong)
            {
                var btcSpec=definition.Catalog.SingleOrDefault(i=>i.BaseCurrency=="BTC"&&i.QuoteCurrency==spec.QuoteCurrency)??await BenchmarkAsync(spec,ct).ConfigureAwait(false);
                if(btcSpec==null){EvaluationCode("SKIPPED_BTC_REFERENCE");return Refuse("CURRENT_BTC_REFERENCE_UNPROVEN");}
                btc=spec==btcSpec?series:await feed.ReadSeriesAsync(btcSpec,ct).ConfigureAwait(false);
            }
            var request=new NavalEvaluationRequest(fleet,series,btc,cutoff,hasLong);var decision=new NavalD1Evaluator().Evaluate(request);
            Evaluated(decision.Action);evaluated=true;
            if(decision.Action==NavalAction.ExitSignal)
            {
                var registered=account.RegisterConfirmedSignal(request);if(registered.Status is not (NavalMutationStatus.Applied or NavalMutationStatus.Duplicate))return registered;
                var exit=await ExitOwned(state.Naval!.Reservations.Single(r=>r.Fleet==fleet&&r.Intent.Instrument==instrument&&r.Inventory>0&&r.QuarantineMark==null).Id,ct,decision.StableId).ConfigureAwait(false);
                if(exit.Status!=NavalMutationStatus.Applied)session.RevokeStrategyContinuation();
                return exit;
            }
            if(decision.Action!=NavalAction.EntrySignal)return Refuse(decision.Reason);
            var registration=account.RegisterConfirmedSignal(request);if(registration.Status is not (NavalMutationStatus.Applied or NavalMutationStatus.Duplicate))return registration;
            state=session.ReadState();string command="d1-"+NavalJson.Hash(decision.StableId);
            if(state.Naval!.Commands.Any(c=>c.SignalId==decision.StableId))return Refuse("SIGNAL_CONSUMED");
            var quote=await feed.ReadQuoteAsync(spec,ct).ConfigureAwait(false);
            if(quote.SourceUtc>clock.GetUtcNow()||clock.GetUtcNow()-quote.SourceUtc>=TimeSpan.FromSeconds(5))return Refuse("QUOTE_STALE");
            var marked=session.UpdateQuote(quote);if(marked.Status!=NavalMutationStatus.Applied)return marked;state=session.ReadState();
            decimal Ceiling(decimal p)=>decimal.Ceiling(p/spec.TickSize)*spec.TickSize;
            decimal Floor(decimal p)=>decimal.Floor(p/spec.TickSize)*spec.TickSize;
            var bars=series.Bars;var profile=NavalFleetProfiles.Get(fleet);if(bars.Count<=profile.ResearchAtrDays)return Refuse("ATR_HISTORY");
            decimal atr=Enumerable.Range(bars.Count-profile.ResearchAtrDays,profile.ResearchAtrDays).Sum(n=>Math.Max(bars[n].High-bars[n].Low,Math.Max(Math.Abs(bars[n].High-bars[n-1].Close),Math.Abs(bars[n].Low-bars[n-1].Close))))/profile.ResearchAtrDays;
            decimal price=Ceiling(quote.Ask),stop=Floor(price-profile.ResearchStopAtr*atr),tp=Ceiling(price+(price-stop)*profile.ResearchTakeProfitR);
            if(stop<=0||stop>=price)return Refuse("INVALID_PROTECTION");
            var unit=new NavalReservation{Intent=new(command,decision.StableId,instrument,1,price,stop,tp),Quantity=1,Pending=1};
            var unitHolds=NavalCashRiskPolicy.Holds(unit,definition,state.Naval!.Marks);decimal equity=state.Naval.LiquidationEquity??0;
            decimal capital=Math.Min(state.Naval.Accounts.Sum(a=>a.Cash)+state.Naval.Reservations.Sum(r=>r.AllocatedCost),equity);
            decimal quantity=Math.Min(quote.AskQuantity,Math.Min(capital*definition.Limits.TradePct/100/unitHolds.RiskHold,state.Naval.Accounts.Single().Cash/NavalCashRiskPolicy.Held(unitHolds)));
            if(OrdinaryDemoPolicy)quantity=Math.Min(quantity,10m/NavalCashRiskPolicy.BuyDebit(1,price,definition.Fees));
            if(definition.Fees.EntryInBase&&definition.Fees.EntryRate>0)
            {
                try{quantity=NetLotQuantity(quantity,spec.Quantum,definition.Fees.EntryRate);}
                catch(OverflowException){return Refuse("NET_LOT_SIZING_UNREPRESENTABLE");}
                if(quantity<spec.MinimumQuantity||quantity*(1-definition.Fees.EntryRate)<spec.MinimumQuantity)return Refuse("NET_LOT_MINIMUM");
            }
            else quantity=decimal.Floor(quantity/spec.Quantum)*spec.Quantum;
            if(Conditional)
            {
                await RefreshLimitReferences(spec,quote,ct).ConfigureAwait(false);
                if(!WithinPrivateMaxima(instrument,quantity,Math.Max(price,tp),true))return Refuse("PRIVATE_MAXIMUM_OR_UNIT_UNPROVEN");
            }
            var intent=new NavalEntryIntent(command,decision.StableId,instrument,quantity,price,stop,tp);
            var reserved=session.Coordinator.ForFleet(baseline.Binding.Account,fleet).TryReserve(intent);if(reserved.Status!=NavalMutationStatus.Applied)return reserved;
            var ids=session.ReserveIds(reserved.ReservationId!,command);if(ids.Result.Status!=NavalMutationStatus.Applied)return ids.Result;
            var deadline=DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().AddSeconds(10).ToUnixTimeMilliseconds());
            var prepared=OkxPreparedRequest.Submit(account.ExecutionScope!,instrument,"buy","fok",N(quantity),N(price),ids.Ids[0].ClientId,deadline,new(ids.Ids[1].ClientId,N(tp),N(stop)));
            state=session.ReadState();var x=state.NavalBroker!.Operational!;string id="entry-"+command;
            var durable=new NavalBrokerIntent(id,reserved.ReservationId!,NavalExecutionOperation.Submit,instrument,prepared.OriginalClientId,null,prepared.CopyBody(),prepared.Digest,deadline,SessionId,state.Naval!.Generation,state.Naval.Sequence,x.StopEpoch,state.DurableRevision+1,NavalExecutionStage.Prepared);
            var stored=session.Prepare(durable);if(stored.Status!=NavalMutationStatus.Applied)return stored;
            current=id;var outcome=await client.SubmitAsync(prepared,ct).ConfigureAwait(false);
            var mapped=new NavalExecutionOutcome(outcome.Disposition==OkxWriteDisposition.Acknowledged?NavalDispatchDisposition.Acknowledged:outcome.Disposition==OkxWriteDisposition.ProvenNotSent?NavalDispatchDisposition.NotSent:NavalDispatchDisposition.Unknown,
                outcome.OriginalClientId,outcome.OrderId,outcome.Disposition==OkxWriteDisposition.Acknowledged?"TRANSPORT_ACK":outcome.Disposition==OkxWriteDisposition.ProvenNotSent?"TRANSPORT_NOT_SENT":"TRANSPORT_UNKNOWN",outcome.Code,outcome.SCode,outcome.SubCode);
            return session.Outcome(id,mapped);
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException or OverflowException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException)
        {EvaluationCode(evaluated?"EXECUTION_FAILED":"EVALUATION_FAILED");return session.Coordinator.Stop("BROKER_LOOP_INCOMPLETE");}
        finally{current=null;lane.Release();}
    }
    // If rate=a/b in lowest terms, gross lots must be a multiple of b for
    // scheduled net lots to be integral. Integer arithmetic avoids decimal's
    // implicit precision rounding, including when deriving the effective lot.
    internal static decimal NetLotQuantity(decimal quantity,decimal quantum,decimal rate)
    {
        if(quantity<0||quantum<=0||rate<0||rate>=1)throw new ArgumentOutOfRangeException();
        static (BigInteger Units,int Scale) Parts(decimal value)
        {
            var bits=decimal.GetBits(value);
            return ((BigInteger)(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64),(bits[3]>>16)&255);
        }
        static decimal Exact(BigInteger units,int scale)
        {
            while(scale>0&&units%10==0){units/=10;scale--;}
            if(units>((BigInteger.One<<96)-1))throw new OverflowException();
            return new decimal(unchecked((int)(uint)(units&uint.MaxValue)),unchecked((int)(uint)((units>>32)&uint.MaxValue)),unchecked((int)(uint)((units>>64)&uint.MaxValue)),false,(byte)scale);
        }
        var fee=Parts(rate);var lot=Parts(quantum);var cap=Parts(quantity);
        BigInteger denominator=BigInteger.Pow(10,fee.Scale);
        BigInteger multiple=denominator/BigInteger.GreatestCommonDivisor(fee.Units,denominator);
        BigInteger effectiveUnits=lot.Units*multiple;
        _=Exact(effectiveUnits,lot.Scale); // Refuse an unrepresentable effective gross lot.
        BigInteger count=cap.Units*BigInteger.Pow(10,lot.Scale)/(effectiveUnits*BigInteger.Pow(10,cap.Scale));
        BigInteger grossUnits=count*effectiveUnits;
        _=Exact(grossUnits*(denominator-fee.Units)/denominator,lot.Scale);
        _=Exact(grossUnits*fee.Units/denominator,lot.Scale);
        return Exact(grossUnits,lot.Scale);
    }
    private static string N(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
    public bool CanDispatch(OkxPreparedRequest request,DateTimeOffset now)=>false;
    public IDisposable? TryEnterDispatch(OkxPreparedRequest request,DateTimeOffset now)=>current==null||!PreparedAdmission(request)?null:session.EnterDispatch(current,request.Digest,now);
    internal void Stop(){RevokeArming();if(ReferenceEquals(account.BrokerSession,session))session.Coordinator.Stop("OPERATOR_STOP");}
    private readonly object closing=new();private Task? disposal;
    public ValueTask DisposeAsync(){lock(closing)return new(disposal??=DrainAsync());}
    private async Task DrainAsync()
    {
        RevokeArming();disposed=true;if(ReferenceEquals(account.BrokerSession,session))session.Coordinator.Stop("BROKER_TRADING_DISPOSED");
        await client.DisposeAsync().ConfigureAwait(false);await lane.WaitAsync().ConfigureAwait(false);
        try{feed.Dispose();account.UnregisterTrading(this);}finally{lane.Release();}
    }
}
