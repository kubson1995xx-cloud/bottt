using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

// Dedicated existing-owner cleanup. No discovery, baseline creation, arming,
// START, new entry, replacement, funding mutation or scheduler. The explicit
// owned-exit branch alone composes the dedicated once-only own-net reduction.
internal sealed partial class FiniteStopRecovery : INavalBrokerClock,INavalBrokerOwnedReductionAuthority,IOkxObservationReadFence,IOkxSerializedDispatchFence
{
    private readonly TimeProvider clock;
    private readonly NavalBrokerSession session;
    private readonly OkxExecutionScope scope;
    private OkxObservationContext context;
    private bool active=true,authenticated;
    private OkxExecutionOrderDto? live;
    private DateTimeOffset observedUtc;private long observedMono;
    private string? cancelId;
    public Guid SessionId { get; }=Guid.NewGuid();
    public bool SourceCurrent=>active&&authenticated&&LinuxInput.SafeForPrivateDispatch();
    public bool CanExecute=>false;
    public string? ProtectionProfile=>null;
    public DateTimeOffset UtcNow=>clock.GetUtcNow();
    public long Timestamp=>clock.GetTimestamp();
    public long TimestampFrequency=>clock.TimestampFrequency;
    private FiniteStopRecovery(TimeProvider clock,NavalBrokerSession session,OkxExecutionScope scope){this.clock=clock;this.session=session;this.scope=scope;context=new(SessionId,Guid.NewGuid());}
    public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset now)=>active&&s==scope&&c==context&&LinuxInput.SafeForPrivateDispatch();
    public bool CanDispatch(OkxPreparedRequest r,DateTimeOffset now)=>false;
    public IDisposable? TryEnterDispatch(OkxPreparedRequest r,DateTimeOffset now)=>ownedCap!=null&&ownedCurrent!=null&&r.Scope==scope&&ownedRequest?.Digest==r.Digest?session.EnterOwnedReduction(ownedCap,ownedCurrent,r.Digest,now):r.Operation==OkxExecutionOperation.Cancel&&r.Scope==scope&&cancelId!=null?session.EnterStopCancel(cancelId,r.Digest,now):null;
    public bool CanCancel(NavalBrokerIntent p,DateTimeOffset now)=>SourceCurrent&&live!=null&&live.InstId==p.Instrument&&live.ClOrdId==p.ClientId&&
        (p.Outcome?.OrderId==null||live.OrdId==p.Outcome.OrderId)&&live.Side=="buy"&&live.TdMode=="cash"&&live.TradeQuoteCcy==scope.QuoteCurrency&&
        live.State is "live" or "partially_filled"&&now>=observedUtc&&now-observedUtc<TimeSpan.FromSeconds(10)&&Timestamp>=observedMono&&(decimal)(Timestamp-observedMono)/TimestampFrequency<10;

    internal static async Task<int> RunAsync(string root,LinuxDemoCredentialVault vault,FiniteEntryTestComposition? test,bool cancel,bool diagnose=false)
    {
        try{return await RunCoreAsync(root,vault,test,cancel,diagnose).ConfigureAwait(false);}
        catch{Console.Error.WriteLine("RECOVERY_REPORT_OR_DURABLE_STOP_UNPROVEN_PRESERVE_STATE");return 7;}
    }
    internal static async Task<int> RunOwnedAsync(string root,LinuxDemoCredentialVault vault,FiniteEntryTestComposition? test)
    {try{return await RunCoreAsync(root,vault,test,false,false,true).ConfigureAwait(false);}catch{Console.Error.WriteLine("OWNED_RECOVERY_REPORT_OR_STOP_UNPROVEN_PRESERVE_STATE");return 7;}}
    internal static async Task<int> RunOwnedDiagnosticAsync(string root,LinuxDemoCredentialVault vault,FiniteEntryTestComposition? test)
    {try{return await RunCoreAsync(root,vault,test,false,true,false,true).ConfigureAwait(false);}catch{Console.Error.WriteLine("OWNED_DIAGNOSTIC_REPORT_OR_STOP_UNPROVEN_PRESERVE_STATE");return 7;}}
    private static async Task<int> RunCoreAsync(string root,LinuxDemoCredentialVault vault,FiniteEntryTestComposition? test,bool cancel,bool diagnose,bool ownedExit=false,bool retrospectiveDiagnostic=false)
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=budget.Token;
        var signals=new List<PosixSignalRegistration>();FileStream? runLock=null;OkxNavalStore? store=null;NavalBrokerSession? owner=null;EphemeralCredentials? credentials=null;
        OkxExecutionObservationClient? reads=null;OkxExecutionClient? writes=null;FiniteStopRecovery? lane=null;
        string runId=Guid.NewGuid().ToString("N"),stage="LOCAL_ADMISSION",result="RECOVERY_NOT_STARTED";string? report=null;var started=DateTimeOffset.UtcNow;var stages=new List<string>();object? observation=null;NavalRiskResult? stop=null;
        void Publish(bool complete=false)
        {
            if(report==null)return;stages.Add(stage);
            byte[] bytes=TransactionReport.Build(runId,started,complete,result,stages.ToArray(),owner?.ReadState(),null,new{stage,stop,observation,reads=reads?.ReadDiagnostics(),writes=writes?.ReadDiagnostics(),newRiskAuthority=false,cancelOnly=cancel,diagnosticOnly=diagnose,ownedReductionOnly=ownedExit,expiredEntryPlanNotRequired=true},false,null,ownedExit?"EXISTING_STOP_OWNER_ONCE_OWNED_REDUCTION":retrospectiveDiagnostic?"EXISTING_OWNER_RETROSPECTIVE_ATTACHMENT_GET_ONLY":diagnose?"EXISTING_OWNER_ORDER_PROTECTION_GET_ONLY":"EXISTING_OWNER_RECOVER_STOP");
            try{if(credentials?.ContainsSecret(bytes)==true)throw new IOException("RECOVERY_REPORT_PRIVACY");OkxLinuxPrivate.Publish(report,bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
        }
        try
        {
            LinuxInput.Harden();foreach(var signal in new[]{PosixSignal.SIGINT,PosixSignal.SIGTERM,PosixSignal.SIGHUP,PosixSignal.SIGQUIT,PosixSignal.SIGTSTP})signals.Add(PosixSignalRegistration.Create(signal,c=>{c.Cancel=true;budget.Cancel();}));
            OkxLinuxPrivate.AdmitDirectory(root,false);Program.AdmitTree(root);
            string lockPath=Path.Combine(root,".run.lock");if(File.Exists(lockPath))OkxLinuxPrivate.AdmitFile(lockPath);
            try{runLock=new(lockPath,new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});}
            catch(IOException){Console.Error.WriteLine("RUN_LOCK_BUSY_NO_CHANGES");return 9;}
            OkxLinuxPrivate.AdmitFile(lockPath);
            // A fixed existing finite-demo target, never enumerate/adopt another scope.
            string authority=Path.Combine(root,"okx","demo","finite-demo","authority"),target=Path.Combine(authority,OkxNavalStore.Target);
            OkxLinuxPrivate.AdmitDirectory(authority,false);OkxLinuxPrivate.AdmitFile(Path.Combine(authority,NavalBrokerValidation.Marker));
            byte[] raw=OkxLinuxPrivate.ReadFile(target);EngineState saved;
            try{using var doc=JsonDocument.Parse(raw);NavalBrokerRaw.Require(doc.RootElement);saved=JsonSerializer.Deserialize<EngineState>(raw,NavalJson.Options)??throw NavalBrokerValidation.Invalid();}finally{CryptographicOperations.ZeroMemory(raw);}
            var b=saved.NavalBroker?.Baseline??throw NavalBrokerValidation.Invalid();var sb=b.Binding;
            var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid=sb.Account.AccountId,ExpectedMainUid=sb.MainUid,QuoteCurrency=sb.Account.QuoteCurrency,CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
            if(sb.Account!=binding.Account||sb.ConfigDigest!=binding.ConfigDigest||sb.ScopeId!=binding.ScopeId||sb.CredentialBindingId!=binding.CredentialBindingId||sb.SafetyVersion!=binding.SafetyVersion||binding.Account.QuoteCurrency is not ("EUR" or "USDC"))throw NavalBrokerValidation.Invalid();
            store=OkxNavalStore.OpenLinuxFiniteDemo(root,binding);owner=NavalBrokerSession.OpenExisting(store,sb,new RecoveryClock(test?.Clock??TimeProvider.System));
            saved=owner.ReadState();var x=saved.NavalBroker!.Operational??throw NavalBrokerValidation.Invalid();
            var parents=x.Intents.Where(i=>i.Operation==NavalExecutionOperation.Submit&&(!ownedExit||!i.Reduction)).ToArray();
            if(parents.Length!=1||parents[0].Reduction||parents[0].DispatchRevision==null||saved.Naval!.Reservations.Count!=1||x.DemoAcceptance==null||x.DemoAcceptance.Instrument!=parents[0].Instrument||parents[0].Stage==NavalExecutionStage.NotSent)throw NavalBrokerValidation.Invalid();
            var parent=parents[0];
            stop=owner.Coordinator.Stop("RECOVERY_STOP_BEFORE_CREDENTIALS");if(owner.ReadState().Naval!.Mode!="STOP"||stop.Code is "STORAGE_FROZEN" or "COMMIT_CONFLICT")throw new IOException("RECOVERY_STOP_NOT_DURABLE");
            string reports=Path.Combine(root,ownedExit?"owned-reduction-reports":retrospectiveDiagnostic?"owned-exit-diagnostic-reports":diagnose?"order-protection-reports":"recovery-reports");OkxLinuxPrivate.AdmitDirectory(reports,true);report=Path.Combine(reports,runId+".json");
            using(var reserve=new FileStream(report,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite})){reserve.Flush(true);}
            stage="EXISTING_SEAL_OWNER_AND_STOP_ADMITTED_BEFORE_DECRYPT";Publish();
            if(ownedExit&&(x.ExitRequested.Count!=0||x.Intents.Count!=1||x.IdAllocations.Count!=1)){result="OWNED_RECOVERY_ALREADY_ATTEMPTED_NO_RETRY_STOP";return 5;}
            if(b.Observations.OperatorPremise is {} historical&&await vault.RecordIdentityAsync(ct).ConfigureAwait(false)!=historical.CredentialRecordDigest)throw new InvalidOperationException("RECOVERY_CREDENTIAL_RECORD_CHANGED");
            using(var bundle=await vault.LoadAsync(ct).ConfigureAwait(false)){if(bundle.Provenance!="DemoTradingApi")throw new InvalidOperationException("RECOVERY_DEMO_PROVENANCE_REQUIRED");credentials=bundle.TransferTo((k,s,p)=>new EphemeralCredentials(k,s,p));}
            if(credentials.Fingerprint!=sb.CredentialFingerprint)throw new InvalidOperationException("RECOVERY_CREDENTIAL_MISMATCH");
            var scope=new OkxExecutionScope("Demo",sb.Account.AccountId,sb.MainUid,sb.Account.QuoteCurrency,sb.CredentialBindingId);
            lane=new(test?.Clock??TimeProvider.System,owner,scope);if(!diagnose)owner.BindTrading(lane);var provider=credentials.ForObservation(scope);
            reads=(test?.Reads??OkxExecutionObservationClient.Create)(scope,provider,credentials.Fingerprint,lane);
            stage="FRESH_SIGNED_ACCOUNT";Publish();
            var account=await reads.ReadAccountAsync(lane.context,ct).ConfigureAwait(false);
            if(account.Status!=OkxObservationStatus.Observed||account.Rows.Count!=1)throw new InvalidOperationException("RECOVERY_ACCOUNT_UNKNOWN");
            var a=account.Rows[0];var expected=b.Observations.Account;
            if(a.Uid!=sb.Account.AccountId||a.MainUid!=sb.MainUid||a.AccountLevel!=expected.Level||a.AccountType!=expected.Type||a.SpotRoleType!=expected.SpotRole||a.Permissions!=expected.Permissions||a.Ip!=expected.Ip||a.FeeType!=expected.FeeType||a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay)throw new InvalidOperationException("RECOVERY_ACCOUNT_CHANGED");
            lane.authenticated=true;
            if(ownedExit)
            {
                lane.ownedCap=owner.OpenOwnedReduction(parent.Id);
                if(lane.ownedCap==null){result="OWNED_RECOVERY_CAPABILITY_DENIED_STOP";return 5;}
                var completed=await lane.ReduceOwned(reads,parent,credentials.Fingerprint,test,()=>writes??=(test?.Writes??OkxExecutionClient.Create)(scope,provider,credentials.Fingerprint,lane),(s,o)=>{stage=s;observation=o;Publish();},ct).ConfigureAwait(false);
                result=completed;return result.StartsWith("OWNED_RECOVERY_COMPLETED_STOP_",StringComparison.Ordinal)?0:5;
            }
            if(diagnose)
            {
                stage=retrospectiveDiagnostic?"RETROSPECTIVE_OWN_ATTACHMENT_AND_ALLOCATED_PROTECTION_GET_ONLY":"EXACT_OWN_ORDER_AND_ALLOCATED_PROTECTION_GET_ONLY";Publish();
                observation=await lane.Diagnose(reads,parent,credentials,Path.Combine(reports,runId),d=>{observation=d;Publish();},ct,retrospectiveDiagnostic).ConfigureAwait(false);
                result="RECOVERY_DIAGNOSTIC_COMPLETE_STOP_HOLD_RETAINED_NO_SELL_AUTHORITY";return 5;
            }
            stage="EXACT_ORIGINAL_CLIENT_LOOKUP";Publish();
            var exact=await reads.ReadOrderAsync(new(parent.Instrument,parent.Outcome?.OrderId,parent.ClientId),lane.context,ct).ConfigureAwait(false);
            if(exact.Status!=OkxObservationStatus.Observed||exact.Rows.Count!=1){result="RECOVERY_LOOKUP_UNKNOWN_STOP_HOLD_RETAINED";await lane.Collect(reads,test?.Market,parent,null,ct).ConfigureAwait(false);return 5;}
            var order=exact.Rows[0];
            if(order.InstId!=parent.Instrument||order.ClOrdId!=parent.ClientId||parent.Outcome?.OrderId!=null&&order.OrdId!=parent.Outcome.OrderId||order.Side!="buy"||order.TdMode!="cash"||order.TradeQuoteCcy!=scope.QuoteCurrency){result="RECOVERY_IDENTITY_CONFLICT_STOP_HOLD_RETAINED";return 5;}
            observation=new{status="Observed",order.InstId,order.OrdId,order.ClOrdId,order.State};stage="EXACT_ORIGINAL_OBSERVED";Publish();
            if(cancel&&order.State is "live" or "partially_filled")
            {
                lane.live=order;lane.observedUtc=exact.ReceiveUtc;lane.observedMono=exact.ReceiveMonotonic;
                var current=owner.ReadState();var op=current.NavalBroker!.Operational!;string id="cancel-"+parent.Id;
                if(op.Intents.Any(i=>i.Operation==NavalExecutionOperation.Cancel&&i.ReservationId==parent.ReservationId)){result="RECOVERY_CANCEL_ALREADY_PREPARED_NO_RETRY_STOP";return 5;}
                var deadline=DateTimeOffset.FromUnixTimeMilliseconds(lane.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
                var request=OkxPreparedRequest.Cancel(scope,parent.Instrument,parent.Outcome?.OrderId,parent.ClientId,deadline);
                var intent=new NavalBrokerIntent(id,parent.ReservationId,NavalExecutionOperation.Cancel,parent.Instrument,parent.ClientId,parent.Outcome?.OrderId,request.CopyBody(),request.Digest,deadline,lane.SessionId,current.Naval!.Generation,current.Naval.Sequence,op.StopEpoch,current.DurableRevision+1,NavalExecutionStage.Prepared);
                var prepared=owner.PrepareStopCancel(intent);if(prepared.Status!=NavalMutationStatus.Applied){result="RECOVERY_CANCEL_PREPARE_REFUSED_STOP";return 5;}
                stage="EXACT_CANCEL_PREPARED";Publish();lane.cancelId=id;
                writes=(test?.Writes??OkxExecutionClient.Create)(scope,provider,credentials.Fingerprint,lane);
                var outcome=await writes.CancelAsync(request,ct).ConfigureAwait(false);lane.cancelId=null;
                var disposition=outcome.Disposition==OkxWriteDisposition.Acknowledged?NavalDispatchDisposition.Acknowledged:outcome.Disposition==OkxWriteDisposition.ProvenNotSent?NavalDispatchDisposition.NotSent:NavalDispatchDisposition.Unknown;
                var recorded=owner.Outcome(id,new(disposition,outcome.OriginalClientId,outcome.OrderId,disposition==NavalDispatchDisposition.Acknowledged?"TRANSPORT_ACK":disposition==NavalDispatchDisposition.NotSent?"TRANSPORT_NOT_SENT":"TRANSPORT_UNKNOWN",outcome.Code,outcome.SCode,outcome.SubCode));
                observation=new{previous=observation,cancelDisposition=disposition.ToString(),recorded.Code};stage="CANCEL_OUTCOME_RECORDED_NOT_TERMINAL_PROOF";Publish();
            }
            stage="TERMINAL_FILLS_FEES_BALANCES_RECONCILIATION";Publish();
            bool terminal=await lane.Collect(reads,test?.Market,parent,order,ct).ConfigureAwait(false);
            result=terminal?"RECOVERY_TERMINAL_OBSERVED_STOP_CHECK_RETAINED_OBLIGATIONS":cancel?"RECOVERY_NOT_TERMINAL_STOP_HOLD_RETAINED":"RECOVERY_READ_ONLY_STOP_HOLD_RETAINED";
            return terminal?0:5;
        }
        catch(OperationCanceledException){result="RECOVERY_CANCELLED_OR_BUDGET_EXPIRED_UNKNOWN_STOP";return 6;}
        catch(Exception){result="RECOVERY_REFUSED_AT_"+stage+"_PRESERVE_STATE";Console.Error.WriteLine(result);return 7;}
        finally
        {
            bool finalFailure=false;
            try
            {
                if(owner!=null){stop=owner.Coordinator.Stop("RECOVERY_FINALLY_STOP");if(owner.ReadState().Naval!.Mode!="STOP"||stop.Code is "STORAGE_FROZEN" or "COMMIT_CONFLICT"){result="RECOVERY_DURABLE_STOP_UNPROVEN_PRESERVE_STATE";finalFailure=true;}}
                stage="RECOVERY_FINALLY_STOP";Publish(true);
                if(report!=null)Console.WriteLine(result+"; private recovery report: "+report);
            }
            catch{finalFailure=true;Console.Error.WriteLine("RECOVERY_REPORT_OR_STOP_UNPROVEN_PRESERVE_STATE");}
            finally
            {
                if(lane!=null)lane.active=false;
                try{if(writes!=null)await writes.DisposeAsync().ConfigureAwait(false);if(reads!=null)await reads.DisposeAsync().ConfigureAwait(false);}
                finally{credentials?.Dispose();owner?.Dispose();store?.Dispose();runLock?.Dispose();foreach(var signal in signals)signal.Dispose();}
            }
            if(finalFailure)throw new IOException("RECOVERY_FINAL_PUBLICATION_FAILED");
        }
    }
    private sealed class RecoveryClock(TimeProvider clock):INavalBrokerClock
    {public DateTimeOffset UtcNow=>clock.GetUtcNow();public long Timestamp=>clock.GetTimestamp();public long TimestampFrequency=>clock.TimestampFrequency;}

    // Typed observations go through the SAME accounting owner. Without a fresh
    // entry-premise this pass deliberately cannot release holds or mint readiness.
    private async Task<bool> Collect(OkxExecutionObservationClient reads,System.Net.Http.HttpMessageHandler? market,NavalBrokerIntent parent,OkxExecutionOrderDto? previous,CancellationToken ct)
    {
        var state=session.ReadState();var b=state.NavalBroker!.Baseline;var start=UtcNow;long mono=Timestamp;var problems=new List<string>{"RECOVERY_NO_ENTRY_OR_FUNDING_AUTHORITY"};var evidence=new List<NavalBrokerSurface>();
        void Add<T>(OkxObservationRead<T> r){if(r.ResponseDigest!=null)evidence.Add(new(r.Endpoint,r.ExactQuery,r.ResponseDigest,r.Rows.Count,r.RequestUtc,r.ReceiveUtc,r.RequestMonotonic,r.ReceiveMonotonic));}
        var exact=await reads.ReadOrderAsync(new(parent.Instrument,parent.Outcome?.OrderId,parent.ClientId),context,ct).ConfigureAwait(false);Add(exact);
        var orders=exact.Status==OkxObservationStatus.Observed?exact.Rows.Where(o=>o.InstId==parent.Instrument&&o.ClOrdId==parent.ClientId&&(parent.Outcome?.OrderId==null||o.OrdId==parent.Outcome.OrderId)&&o.Side=="buy"&&o.TdMode=="cash"&&o.TradeQuoteCcy==scope.QuoteCurrency).ToArray():Array.Empty<OkxExecutionOrderDto>();
        if(orders.Length!=1)problems.Add("ORIGINAL_ORDER_UNKNOWN:"+parent.Id);
        var fills=new List<OkxExecutionFillDto>();var bills=new List<OkxExecutionBillDto>();var pager=new OkxObservationPager(reads);
        var begin=DateTimeOffset.FromUnixTimeMilliseconds(b.Observations.CaptureUtc.ToUnixTimeMilliseconds());var end=DateTimeOffset.FromUnixTimeMilliseconds(UtcNow.ToUnixTimeMilliseconds());if(end<=begin)end=begin.AddMilliseconds(1);
        foreach(var surface in new[]{OkxObservationSurface.Fills,OkxObservationSurface.FillsArchive,OkxObservationSurface.Bills,OkxObservationSurface.BillsArchive})
        {
            var collection=await pager.CollectAsync(new(surface,new(null,null,begin,end,null),context),ct).ConfigureAwait(false);
            if(collection.Status!=OkxObservationCollectionStatus.Collected)problems.Add("HISTORY_INCOMPLETE:"+surface);fills.AddRange(collection.Fills);bills.AddRange(collection.Bills);
            foreach(var p in collection.Trace)if(p.ResponseDigest!=null)evidence.Add(new(p.Endpoint,p.ExactQuery,p.ResponseDigest,p.RowCount,p.RequestUtc,p.ReceiveUtc,p.RequestMonotonic,p.ReceiveMonotonic));
        }
        var protection=new List<NavalBrokerProtection>();
        foreach(var o in orders)foreach(var a in o.AttachAlgoOrds?.ToArray()??Array.Empty<OkxExecutionAttachmentDto>())
        {
            // Do not guess numeric generated IDs or cancel attachments. Missing or
            // conflicting protection remains an obligation in the original owner.
            if(string.IsNullOrEmpty(a.AttachAlgoClOrdId)||!state.NavalBroker.Operational!.IdAllocations.SelectMany(v=>v.Ids).Any(id=>id.Role==NavalClientIdRole.ProtectionAlgo&&id.ReservationId==parent.ReservationId&&id.ClientId==a.AttachAlgoClOrdId)){problems.Add("RECOVERY_PROTECTION_UNPROVEN");continue;}
            var detail=await reads.ReadAlgoAsync(new(null,a.AttachAlgoClOrdId),context,ct).ConfigureAwait(false);Add(detail);
            if(detail.Status!=OkxObservationStatus.Observed||detail.Rows.Count!=1||string.IsNullOrEmpty(a.AttachAlgoId)){problems.Add("RECOVERY_PROTECTION_UNPROVEN");continue;}
            var p=detail.Rows[0];if(p.InstId!=parent.Instrument||p.AlgoClOrdId!=a.AttachAlgoClOrdId||p.Side!="sell"||p.TdMode!="cash"||p.TradeQuoteCcy!=scope.QuoteCurrency){problems.Add("RECOVERY_PROTECTION_IDENTITY_CONFLICT");continue;}
            protection.Add(new(p.InstId,o.OrdId,a.AttachAlgoClOrdId,p.AlgoId,p.State,p.Sz.Value,p.SlTriggerPx.Value,p.TpTriggerPx.Value,p.OrdIdList?.ToArray()??Array.Empty<string>(),p.PayloadDigest){AttachmentId=a.AttachAlgoId});
        }
        var balance=await reads.ReadBalancesAsync(context,ct).ConfigureAwait(false);Add(balance);
        var balances=balance.Status==OkxObservationStatus.Observed&&balance.Rows.Count==1?balance.Rows[0].Details.Select(OkxBrokerObservationMapper.MapBalance).ToArray():Array.Empty<NavalBrokerBalance>();
        if(balances.Length==0)problems.Add("BALANCE_UNKNOWN");
        var raw=fills.GroupBy(f=>(f.InstId,f.TradeId,f.PayloadDigest)).Select(g=>g.First()).Select(f=>new NavalBrokerRawFill(f.InstId,f.OrdId,f.ClOrdId,f.TradeId,f.BillId,f.Side??"",f.FillSz.Value,f.FillPx.Value,f.Fee.Value,f.FeeCcy,f.TradeQuoteCcy,f.FillTime.Utc,f.PayloadDigest)).ToArray();
        var billRows=bills.GroupBy(v=>(v.BillId,v.PayloadDigest)).Select(g=>g.First()).Select(v=>new NavalBrokerRawBill(v.BillId,v.InstId,v.OrdId,v.ClOrdId,v.TradeId,v.Type,v.Ccy,v.Fee.Value,v.BalChg.Value,v.Ts.Utc,v.PayloadDigest)).ToArray();
        var proofs=orders.Select(o=>{var a=o.AttachAlgoOrds?.FirstOrDefault();var p=protection.SingleOrDefault(v=>v.ParentOrderId==o.OrdId&&v.AttachmentClientId==a?.AttachAlgoClOrdId);return new NavalBrokerOrderProof(o.InstId,o.OrdId,o.ClOrdId,o.State,o.Sz.Value,o.AccFillSz.Value,o.Fee.Value,o.FeeCcy,o.TradeQuoteCcy,a?.AttachAlgoClOrdId,p?.AlgoId,o.PayloadDigest){AttachmentId=a?.AttachAlgoId};}).ToArray();
        session.ApplyEconomics(new(start,mono,false,raw,billRows,proofs,protection,balances,new Dictionary<string,NavalBrokerQuote>(),evidence,problems){CurrentMarketFunding=state.NavalBroker.Operational!.LastCurrentMarketFunding,OperatorPremise=b.Observations.OperatorPremise,ObservationCapture=context.CaptureId});
        return orders.Length==1&&orders[0].State is "filled" or "canceled" or "mmp_canceled";
    }
}
