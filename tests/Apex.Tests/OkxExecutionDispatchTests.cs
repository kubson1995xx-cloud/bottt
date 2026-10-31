using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxExecutionDispatchTests
{
    internal sealed class Clock(OkxExecutionFixture f) : TimeProvider
    {public override DateTimeOffset GetUtcNow()=>f.Clock.Now;}
    internal sealed class Peer(OkxExecutionFixture f,OkxPreparedRequest expected) : HttpMessageHandler,IOkxSigningMaterialProvider
    {
        internal int Calls,Opens;
        internal Action? AtInvocation;
        internal Func<CancellationToken,Task<HttpResponseMessage>>? Reply;
        internal TaskCompletionSource<bool>? Opened,ReleaseOpen;
        internal const string Key="SYNTHETIC_key",Secret="SYNTHETIC_secret",Pass="SYNTHETIC_pass";
        internal string Fingerprint=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant();
        public async ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
        {
            Opens++;Opened?.TrySetResult(true);if(ReleaseOpen!=null)await ReleaseOpen.Task.WaitAsync(ct);
            return new(f.Scope,"DemoTradingApi",Encoding.UTF8.GetBytes(Key),Encoding.UTF8.GetBytes(Secret),Encoding.UTF8.GetBytes(Pass));
        }
        internal HttpResponseMessage Ack()=>new(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"ordId\":\"987654\",\"clOrdId\":\""+expected.OriginalClientId+"\",\"ts\":\"1767268800000\",\"sCode\":\"0\",\"sMsg\":\"\",\"subCode\":\"\"}]}")};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Calls++;Assert.Equal("https://eea.okx.com"+expected.Path,r.RequestUri!.AbsoluteUri);Assert.Equal(expected.Method,r.Method.Method);
            // ByteArrayContent completes synchronously: assertions are at actual synchronous invocation, not response await.
            var body=r.Content!.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();Assert.Equal(expected.CopyBody(),body);
            Assert.Equal("application/json",r.Content.Headers.ContentType!.MediaType);
            Assert.Equal("1",Assert.Single(r.Headers.GetValues("x-simulated-trading")));
            Assert.Equal(Key,Assert.Single(r.Headers.GetValues("OK-ACCESS-KEY")));Assert.Equal(Pass,Assert.Single(r.Headers.GetValues("OK-ACCESS-PASSPHRASE")));
            string stamp=Assert.Single(r.Headers.GetValues("OK-ACCESS-TIMESTAMP"));Assert.Equal(f.Clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),stamp);
            string signature=Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),Encoding.UTF8.GetBytes(stamp+expected.Method+expected.Path+Encoding.UTF8.GetString(body))));
            Assert.Equal(signature,Assert.Single(r.Headers.GetValues("OK-ACCESS-SIGN")));
            if(expected.Operation==OkxExecutionOperation.Submit)Assert.Equal(expected.Deadline.ToUnixTimeMilliseconds().ToString(),Assert.Single(r.Headers.GetValues("expTime")));
            else Assert.False(r.Headers.Contains("expTime"));
            AtInvocation?.Invoke();return Reply?.Invoke(ct)??Task.FromResult(Ack());
        }
    }
    internal static OkxPreparedRequest Prepare(OkxExecutionFixture f)
    {
        f.Run();var r=f.Reserve();Assert.Equal(NavalMutationStatus.Applied,r.Status);f.Init();
        var ids=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo});Assert.Equal(NavalMutationStatus.Applied,ids.Result.Status);
        var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids.Ids[0].ClientId,f.Clock.Now.AddSeconds(5),new(ids.Ids[1].ClientId,"130","90"));
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(r.ReservationId!,f.Intent(r,wire)).Status);f.Holds();return wire;
    }
    internal static OkxNavalExecutionDispatcher Dispatcher(OkxExecutionFixture f,Peer peer,Func<TimeSpan,CancellationToken,Task>? delay=null)
        =>OkxNavalRuntime.ComposeOfflineExecutionForTests(f.Transactions,fence=>OkxExecutionClient.ForTests(peer,new Clock(f),f.Scope,peer,peer.Fingerprint,fence,delay??((t,ct)=>{f.Clock.Now+=t;return Task.CompletedTask;})));
    [Fact]
    public void CommonCommitCannotAppendAlreadyCompletedIntent()
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();var ids=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent}).Ids;
        var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids[0].ClientId,f.Clock.Now.AddSeconds(5));
        var intent=f.Intent(r,wire) with{PreparationSessionId=Guid.NewGuid().ToString("N"),Stage=NavalExecutionStage.NotSent,Outcome=new(NavalDispatchDisposition.NotSent,ids[0].ClientId,null,"TRANSPORT_NOT_SENT",null,null,null)};
        var old=f.Owner.Read();var candidate=NavalJson.Clone(old);candidate.DurableRevision++;candidate.Naval=candidate.Naval! with{Revision=candidate.DurableRevision};candidate.NavalExecution=candidate.NavalExecution! with{Intents=new[]{intent}};
        candidate.NavalReadJournal=NavalReadJournal.Append(old,candidate,new(NavalPublicationOperation.ExecutionMaintenance,"",Guid.NewGuid().ToString("N")),f.Clock.Now,f.Definition);
        string disk=System.IO.File.ReadAllText(f.Target);Assert.Equal(NavalCommitResult.Faulted,f.Owner.TryCommit(old.DurableRevision,candidate));Assert.Equal(disk,System.IO.File.ReadAllText(f.Target));f.Holds();
    }
    [Fact]
    public void DispatchingObligationCannotGrantAnotherStartOrOfflineSettlement()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);var session=f.Transactions.OpenOfflineSession();Assert.Equal(NavalMutationStatus.Applied,f.Transactions.ClaimDispatch("execution-0").Status);
        using(var guard=f.Transactions.TryEnterDispatch("execution-0",wire.Digest,session,f.Clock.Now))Assert.NotNull(guard);
        var e=f.Owner.Read();Assert.Equal(NavalMutationStatus.Refused,f.Coordinator.Start(new(f.Coordinator.OwnerToken,e.DurableRevision,e.Naval!.Generation,true)).Status);
        var s=f.Owner.Read().Naval!;var id=s.Reservations.Single().Id;
        Assert.Equal(NavalMutationStatus.Stopped,f.Coordinator.Terminal(new(f.Coordinator.OwnerToken,f.Definition.Id,"cannot-settle",s.Generation,s.Sequence+1,f.Clock.Now,f.Clock.Now,NavalEvidenceKind.Terminal,true,true,s.Marks,null,s.Accounts,new[]{new NavalObservation(id,0,0)},new[]{id})).Status);
        f.Holds();Assert.Equal(NavalExecutionStage.Dispatching,f.Transactions.Read("execution-0")!.Stage);
    }
    [Fact]
    public async Task OkxExecution_ProductionCompositionStillHasNoAuthority()
    {
        string root=System.IO.Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")??System.IO.Path.GetTempPath(),"okx-d-production-"+Guid.NewGuid().ToString("N"));
        try
        {
            for(int restart=0;restart<2;restart++)
            {
                await using var runtime=OkxNavalRuntime.Compose(root,new[]{CredentialFixture.Binding()});await runtime.StartAsync(default);
                Assert.Equal("STOP",runtime.Mode);Assert.Equal("ACTIVATION_REQUIRED",runtime.Reason);
                var lazy=(Lazy<IOkxExecutionSecretProvider>)typeof(OkxNavalRuntime).GetField("secrets",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!;
                Assert.False(lazy.IsValueCreated);Assert.False(System.IO.Directory.Exists(root));
            }
        }
        finally{if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
    }
    [Theory][InlineData("catalog")][InlineData("config")]
    public async Task ImmutableDefinitionReplacementClosesOldSession(string kind)
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire){Opened=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseOpen=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var d=Dispatcher(f,peer);var pending=d.DispatchAsync("execution-0",default);await peer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // There is no mutable config/catalog setter. Replacement must first close the old owner session.
        f.Transactions.OpenOfflineSession().Revoke();
        var changed=new NavalPortfolioDefinition(f.Definition.Id,kind=="config"?new NavalCashLimits(TradePct:.4m):f.Definition.Limits,f.Definition.Members,
            kind=="catalog"?f.Definition.Catalog.Select(i=>i with{Quantum=.01m}):f.Definition.Catalog,f.Definition.Fees);
        Assert.Throws<InvalidOperationException>(()=>new NavalPortfolioCoordinator(f.Owner,changed,f.Clock));peer.ReleaseOpen.SetResult(true);await pending.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,peer.Calls);f.Holds();
    }
    [Fact]
    public async Task FreshCommandAndFreshIdCannotReplaceUnresolvedAcceptedEntry()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var first=new Peer(f,wire);await using var d=Dispatcher(f,first);await d.DispatchAsync("execution-0",default);
        var reservation=f.Owner.Read().Naval!.Reservations.Single();var ids=f.Transactions.ReserveIds(reservation.Id,"fresh-entry",new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo});Assert.Equal(NavalMutationStatus.Applied,ids.Result.Status);
        var fresh=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids.Ids[0].ClientId,wire.Deadline,new(ids.Ids[1].ClientId,"130","90"));
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(reservation.Id,f.Intent(new(NavalMutationStatus.Applied,"",reservation.Id,0),fresh,"fresh-entry")).Status);
        using var peer=new Peer(f,fresh);await using var other=Dispatcher(f,peer);Assert.Equal("EXECUTION_WORKFLOW_UNSUPPORTED",(await other.DispatchAsync("fresh-entry",default)).Code);Assert.Equal(0,peer.Calls);Assert.Equal(0,peer.Opens);f.Holds();
    }
    [Theory][InlineData("reason")][InlineData("ack-error-code")][InlineData("unknown-without-financial-uncertainty")]
    public async Task RehashedOutcomeCannotInventAcknowledgementOrDropUncertainty(string mutation)
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire);var d=Dispatcher(f,peer);await d.DispatchAsync("execution-0",default);await d.DisposeAsync();
        var valid=f.Owner.Read();string control=NavalJson.Serialize(valid);var x=valid.NavalExecution!;var original=x.Intents.Single();var outcome=original.Outcome!;
        var changed=mutation switch{
            "reason"=>original with{Outcome=outcome with{Reason="SERVER_TEXT"}},
            "ack-error-code"=>original with{Outcome=outcome with{SCode="51000"}},
            _=>original with{Stage=NavalExecutionStage.SubmitUnknown,Outcome=outcome with{Disposition=NavalDispatchDisposition.Unknown,Reason="TRANSPORT_UNKNOWN"}}};
        valid.NavalExecution=NavalExecutionValidation.Seal(x with{Intents=new[]{changed}},valid.DurableRevision);
        OkxExecutionCompatibilityTests.RefusesUnchanged(f,NavalJson.Serialize(valid));
        System.IO.File.WriteAllText(f.Target,control);f.Open();f.Init();f.Holds();Assert.Equal(NavalExecutionStage.Accepted,f.Transactions.Read("execution-0")!.Stage);
    }
    [Fact]
    public void SourceStopRevokesSessionAndAdvancesDurableEpoch()
    {
        using var f=new OkxExecutionFixture();Prepare(f);var session=f.Transactions.OpenOfflineSession();var before=f.Owner.Read();var s=before.Naval!;
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Reconcile(new(f.Coordinator.OwnerToken,f.Definition.Id,"refresh-stop",s.Generation,s.Sequence+1,f.Clock.Now,f.Clock.Now,NavalEvidenceKind.Reconcile,true,false,s.Marks,null,s.Accounts,new[]{new NavalObservation(s.Reservations.Single().Id,0,4)},null)).Status);
        Assert.True(session.Revoked,"Committed source STOP must revoke private session, not just invalidate old request stamps");Assert.Equal(before.NavalExecution!.StopEpoch+1,f.Owner.Read().NavalExecution!.StopEpoch);
    }
    [Fact]
    public void RevocationIsImmediateEvenWhenDurableStopFails()
    {
        bool armed=false;using var f=new OkxExecutionFixture(observer:b=>{if(armed)throw new System.IO.IOException("SYNTHETIC_STOP_FAULT");});var wire=Prepare(f);var session=f.Transactions.OpenOfflineSession();var before=f.Owner.Read();armed=true;
        session.Revoke();Assert.True(session.Revoked);Assert.Equal(NavalJson.Serialize(before),NavalJson.Serialize(f.Owner.Read()));
        Assert.Null(f.Transactions.TryEnterDispatch("execution-0",wire.Digest,session,f.Clock.Now));Assert.Throws<System.IO.InvalidDataException>(()=>f.Transactions.OpenOfflineSession());f.Holds();
    }
    [Fact]
    public void OkxExecution_EntryFinalityDoesNotSettleReduction()
    {
        using var f=new OkxExecutionFixture();Prepare(f);var r=f.Owner.Read().Naval!.Reservations.Single();
        NavalEvidenceReceipt Receipt(NavalEvidenceKind kind,bool complete=false,bool final=false,NavalFill[]? fills=null,string[]? terminal=null)
        {var s=f.Owner.Read().Naval!;return new(f.Coordinator.OwnerToken,f.Definition.Id,"finality-"+(s.Sequence+1),s.Generation,s.Sequence+1,f.Clock.Now,f.Clock.Now,kind,complete,final,s.Marks,fills,s.Accounts,new[]{new NavalObservation(r.Id,s.Reservations.Single().Inventory,terminal==null?s.Reservations.Single().Pending:0)},terminal);}
        var buy=new NavalFill(f.Account,"BTC-USD","offline-buy",r.Id,false,4,100,.4m,"USD",f.Clock.Now);
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Confirm(Receipt(NavalEvidenceKind.Confirm,fills:new[]{buy})).Status);
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Terminal(Receipt(NavalEvidenceKind.Terminal,true,true,terminal:new[]{r.Id})).Status);
        var sell=buy with{TradeId="offline-reduce",Reduction=true,Price=110,Fee=.44m};
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Confirm(Receipt(NavalEvidenceKind.Confirm,fills:new[]{sell})).Status);
        var before=f.Owner.Read();var i=f.Transactions.Read("execution-0")!;
        // No supported dispatch can be newly admitted against zero inventory/unsettled reduction.
        var attemptedAck=new NavalExecutionOutcome(NavalDispatchDisposition.Acknowledged,i.OriginalClientId,"987654","TRANSPORT_ACK","0","0",null);
        Assert.Equal(NavalMutationStatus.Refused,f.Transactions.RecordOutcome(i.IntentId,new(null,attemptedAck)).Status);
        Assert.Equal(NavalJson.Serialize(before),NavalJson.Serialize(f.Owner.Read()));Assert.True(before.Naval!.Reservations.Single().ReductionUnsettled);Assert.Equal(.520m,f.Coordinator.Snapshot().Held);Assert.Equal(10039.160m,f.Coordinator.Snapshot().Cash);
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.RecordOutcome(i.IntentId,new(null,new(NavalDispatchDisposition.NotSent,i.OriginalClientId,null,"TRANSPORT_NOT_SENT",null,null,null))).Status);
        Assert.True(f.Owner.Read().Naval!.Reservations.Single().ReductionUnsettled);Assert.Equal(.520m,f.Coordinator.Snapshot().Held);
        f.Restart();f.Init();f.Run();Assert.True(f.Owner.Read().Naval!.Reservations.Single().ReductionUnsettled);Assert.Equal(.520m,f.Coordinator.Snapshot().Held);
        Assert.Equal("SYMBOL_OWNED",f.Coordinator.ForFleet(f.Account,NavalFleet.Bravo).TryReserve(new("bravo-command","bravo-0","BTC-USD",4,100,90,130)).Code);
        Assert.Equal(1,f.Coordinator.Snapshot().ActiveCount);Assert.Equal(0m,f.Coordinator.Snapshot().Reservations.Single().Inventory);
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Terminal(Receipt(NavalEvidenceKind.Terminal,true,true,terminal:new[]{r.Id})).Status);Assert.Equal(0m,f.Coordinator.Snapshot().Held);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task RealQuotaWait_StopWinsOrValidControlInvokes(bool stopWins)
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);
        using(var prime=new Peer(f,wire))
        {await using var transport=OkxExecutionClient.ForTests(prime,new Clock(f),f.Scope,prime,prime.Fingerprint,new ProtocolOnlyFence(),(t,ct)=>{f.Clock.Now+=t;return Task.CompletedTask;});
            Assert.Equal(OkxWriteDisposition.Acknowledged,(await transport.SubmitAsync(wire,default)).Disposition);Assert.Equal(1,prime.Calls);}
        var paused=new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var peer=new Peer(f,wire);await using var d=Dispatcher(f,peer,async(t,ct)=>{paused.SetResult(t);await release.Task.WaitAsync(ct);f.Clock.Now+=t;});
        var pending=d.DispatchAsync("execution-0",default);var wait=await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(wait>TimeSpan.Zero);Assert.Equal(0,peer.Opens);
        if(stopWins)f.Coordinator.Stop("fixture-stop");release.SetResult(true);await pending.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(stopWins?0:1,peer.Calls);f.Holds();
    }
    private sealed class ProtocolOnlyFence : IOkxFinalDispatchFence {public bool CanDispatch(OkxPreparedRequest r,DateTimeOffset now)=>true;}
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public OkxExecutionDispatchTests(Xunit.Abstractions.ITestOutputHelper output){this.output=output;}
    [Theory][InlineData("dispatch")][InlineData("ack")]
    public async Task OwnedProcessKilledAfterDurableDispatchingBeforeSendOrAfterAck(string checkpoint)
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo}).Result.Status);
        f.Owner.Dispose();string barrier=f.DirectoryPath+"-d-barrier";
        var start=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string a in new[]{typeof(SyntheticHostEntry).Assembly.Location,"--okx-d-crash",f.DirectoryPath,barrier,checkpoint})start.ArgumentList.Add(a);
        start.Environment["APEX_OKX_PUBLIC_READ_SMOKE"]="0";output.WriteLine("Owned command: "+start.FileName+" "+string.Join(" ",start.ArgumentList));
        using var child=System.Diagnostics.Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(!System.IO.File.Exists(barrier)){Assert.False(child.HasExited,"Child exited before crash checkpoint");await Task.Delay(10,timeout.Token);}
            Assert.Equal(checkpoint=="dispatch"?"READY Dispatching calls=0":"READY Ack calls=1",System.IO.File.ReadAllText(barrier));
            child.Kill(entireProcessTree:false);await child.WaitForExitAsync(timeout.Token);Assert.NotEqual(0,child.ExitCode);Assert.Empty(await stdout);Assert.Empty(await stderr);
            output.WriteLine("Killed/reaped pid "+child.Id+" exit "+child.ExitCode+" at "+checkpoint);
            string actual=System.IO.File.ReadAllText(f.Target);var marker=System.IO.File.ReadAllBytes(System.IO.Path.Combine(f.DirectoryPath,".naval-initialized"));
            var candidates=System.IO.Directory.GetFiles(f.DirectoryPath,"*.tmp").ToDictionary(p=>p,p=>System.IO.File.ReadAllBytes(p));
            f.Open();f.Init();f.Holds();Assert.Equal(actual,System.IO.File.ReadAllText(f.Target));Assert.Equal(marker,System.IO.File.ReadAllBytes(System.IO.Path.Combine(f.DirectoryPath,".naval-initialized")));
            foreach(var pair in candidates)Assert.Equal(pair.Value,System.IO.File.ReadAllBytes(pair.Key));
            var e=f.Owner.Read();var i=Assert.Single(e.NavalExecution!.Intents);Assert.Equal(NavalExecutionStage.Dispatching,i.Stage);Assert.Null(i.Outcome);Assert.Equal(i.PreparedRevision+1,i.DispatchRevision);
            Assert.Equal(NavalReservationStage.Uncertain,f.Coordinator.Snapshot().Reservations.Single().Stage);Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);
            Assert.Equal(e.DurableRevision,e.NavalExecution.FinancialRevision);Assert.Equal(e.DurableRevision,e.NavalReadJournal!.Checkpoint.FinancialRevision);
            f.Generation=e.Naval!.Generation+1;var s=e.Naval;
            Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Reconcile(new(f.Coordinator.OwnerToken,f.Definition.Id,"restart",f.Generation,1,f.Clock.Now,f.Clock.Now,NavalEvidenceKind.Reconcile,true,false,s.Marks,null,s.Accounts,new[]{new NavalObservation(r.ReservationId!,0,4)},null)).Status);
            Assert.Equal(NavalMutationStatus.Refused,f.Coordinator.Start(new(f.Coordinator.OwnerToken,f.Owner.Read().DurableRevision,f.Generation,true)).Status);Assert.Throws<System.IO.InvalidDataException>(()=>f.Transactions.OpenOfflineSession());f.Holds();
            output.WriteLine("Fresh-store target revision "+e.DurableRevision+"; preserved orphan candidates "+candidates.Count+"; no retry/economics");
        }
        finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}System.IO.File.Delete(barrier);}
    }
    [Fact]
    public async Task EnvelopeFailureCodesAndUnexpectedRejectedAreConservative()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire);
        peer.Reply=ct=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"1\",\"msg\":\"PRIVATE\",\"data\":[{\"ordId\":\"\",\"clOrdId\":\""+wire.OriginalClientId+"\",\"ts\":\"1767268800000\",\"sCode\":\"51000\",\"sMsg\":\"PRIVATE\"}]}")});
        await using var d=Dispatcher(f,peer);await d.DispatchAsync("execution-0",default);
        var i=f.Transactions.Read("execution-0")!;Assert.Equal(NavalExecutionStage.SubmitUnknown,i.Stage);Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);Assert.Null(i.Outcome!.Code);Assert.Equal("51000",i.Outcome.SCode);f.Holds();
        foreach(var o in new[]{new OkxWriteOutcome(OkxWriteDisposition.Rejected,i.OriginalClientId,null,"PRIVATE", "1","51000"),
            new OkxWriteOutcome(OkxWriteDisposition.Acknowledged,"foreign","123","PRIVATE","0","0"),
            new OkxWriteOutcome(OkxWriteDisposition.Acknowledged,i.OriginalClientId,"999","PRIVATE","0","0")})
        {
            var original=i with{OrderId="987654"};var mapped=OkxNavalExecutionDispatcher.MapOutcome(original,o);
            Assert.Equal(NavalDispatchDisposition.Unknown,mapped.Disposition);Assert.Equal(original.OriginalClientId,mapped.OriginalClientId);Assert.Equal(original.OrderId,mapped.OrderId);Assert.DoesNotContain("PRIVATE",NavalJson.Serialize(mapped));
        }
    }
    [Theory]
    [InlineData("BeforeTempCreate",false)][InlineData("AfterTempWrite",false)][InlineData("AfterTempFlush",false)][InlineData("BeforeReplace",false)][InlineData("AfterReplace",false)][InlineData("BeforeSet",false)][InlineData("OwnerFlush",false)]
    [InlineData("BeforeTempCreate",true)][InlineData("AfterTempWrite",true)][InlineData("AfterTempFlush",true)][InlineData("BeforeReplace",true)][InlineData("AfterReplace",true)][InlineData("BeforeSet",true)][InlineData("OwnerFlush",true)]
    public async Task ActualFileStore_DispatchAndOutcomeFaultMatrix(string boundary,bool outcomeCommit)
    {
        bool armed=false;int observed=0;
        using var f=new OkxExecutionFixture(observer:b=>{if(armed&&b.ToString()==boundary){observed++;throw new System.IO.IOException("SYNTHETIC_FILE_FAULT");}},wrapStore:s=>new FaultStore(s,()=>armed,boundary));
        var wire=Prepare(f);using var peer=new Peer(f,wire);var before=f.Owner.Read();string publication=NavalJson.Serialize(f.Coordinator.ReadPublication());
        if(outcomeCommit)peer.AtInvocation=()=>{before=f.Owner.Read();publication=NavalJson.Serialize(f.Coordinator.ReadPublication());armed=true;};else armed=true;
        var d=Dispatcher(f,peer);var result=await d.DispatchAsync("execution-0",default);
        Assert.Equal(NavalMutationStatus.Stopped,result.Status);Assert.Equal(outcomeCommit?1:0,peer.Calls);Assert.Equal(boundary is "BeforeSet" or "OwnerFlush"?0:1,observed);
        Assert.Equal(NavalJson.Serialize(before),NavalJson.Serialize(f.Owner.Read()));Assert.Equal(publication,NavalJson.Serialize(f.Coordinator.ReadPublication()));f.Holds();
        string actual=System.IO.File.ReadAllText(f.Target);var repeated=await d.DispatchAsync("execution-0",default);
        Assert.Contains(repeated.Status,new[]{NavalMutationStatus.Stopped,NavalMutationStatus.Duplicate});Assert.Equal(outcomeCommit?1:0,peer.Calls);
        await d.DisposeAsync();Assert.Equal(actual,System.IO.File.ReadAllText(f.Target));armed=false;
        f.Restart();f.Init();f.Holds();Assert.Equal(actual,System.IO.File.ReadAllText(f.Target));var e=f.Owner.Read();var i=Assert.Single(e.NavalExecution!.Intents);
        bool landed=boundary is "AfterReplace" or "OwnerFlush";
        Assert.Equal(outcomeCommit?(landed?NavalExecutionStage.Accepted:NavalExecutionStage.Dispatching):(landed?NavalExecutionStage.Dispatching:NavalExecutionStage.Prepared),i.Stage);
        Assert.Equal(e.DurableRevision,e.NavalExecution.FinancialRevision);Assert.Equal(e.DurableRevision,e.NavalReadJournal!.Checkpoint.FinancialRevision);
        Assert.Equal(wire.CopyBody(),i.Body);Assert.Equal(wire.Digest,i.RequestDigest);Assert.Equal(wire.Deadline,i.Deadline);Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);
        if(i.Stage==NavalExecutionStage.Dispatching)Assert.Equal(NavalReservationStage.Uncertain,f.Coordinator.Snapshot().Reservations.Single().Stage);
    }
    private sealed class FaultStore(Apex.Core.Engine.IKeyValueStore inner,Func<bool> armed,string boundary) : Apex.Core.Engine.IKeyValueStore
    {
        public string? Get(string key)=>inner.Get(key);
        public void Set(string key,string value){if(armed()&&boundary=="BeforeSet")throw new System.IO.IOException("SYNTHETIC_BEFORE_SET");inner.Set(key,value);}
        public void Flush(){if(armed()&&boundary=="OwnerFlush")throw new System.IO.IOException("SYNTHETIC_OWNER_FLUSH_NOT_FSYNC");inner.Flush();}
    }
    [Fact]
    public async Task DispatchPersistenceCrossesDeadline_DoesNotInvoke()
    {
        bool armed=false;OkxExecutionFixture? fixture=null;
        using var f=fixture=new OkxExecutionFixture(observer:b=>{if(armed&&b==Apex.Desktop.Services.AtomicWriteBoundary.AfterReplace)fixture!.Clock.Now=new(2026,1,1,12,0,5,TimeSpan.Zero);});
        var wire=Prepare(f);using var peer=new Peer(f,wire);var d=Dispatcher(f,peer);armed=true;
        await d.DispatchAsync("execution-0",default);Assert.Equal(0,peer.Calls);var i=f.Transactions.Read("execution-0")!;
        Assert.NotNull(i.DispatchRevision);Assert.Equal(NavalExecutionStage.NotSent,i.Stage);f.Holds();armed=false;await d.DisposeAsync();
    }
    [Fact]
    public void OfflineSessionCannotSerializeOrRevivePreparedAfterRestart()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);var session=f.Transactions.OpenOfflineSession();
        Assert.Throws<NotSupportedException>(()=>System.Text.Json.JsonSerializer.Serialize(session));
        session.Revoke();Assert.Null(f.Transactions.TryEnterDispatch("execution-0",wire.Digest,session,f.Clock.Now));
        f.Restart();f.Init();f.Run();var fresh=f.Transactions.OpenOfflineSession();
        Assert.Null(f.Transactions.TryEnterDispatch("execution-0",wire.Digest,fresh,f.Clock.Now));f.Holds();
    }
    [Theory]
    [InlineData("socket")][InlineData("cancel")][InlineData("50004")][InlineData("51000")][InlineData("50011")][InlineData("51016")][InlineData("429")][InlineData("503")][InlineData("invalid")][InlineData("mismatched")]
    public async Task OkxExecution_NoRetryAfterLostAckOrOutcomePersistenceFault(string fault)
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire);using var cancel=new CancellationTokenSource();
        peer.Reply=ct=>{
            if(fault=="socket")throw new HttpRequestException("SYNTHETIC_PRIVATE_EXCEPTION");
            if(fault=="cancel"){cancel.Cancel();return Task.FromCanceled<HttpResponseMessage>(cancel.Token);}
            var ack=peer.Ack();
            if(fault is "429" or "503")ack.StatusCode=(HttpStatusCode)int.Parse(fault);
            else if(fault=="invalid")ack.Content=new StringContent("PRIVATE_RESPONSE_SENTINEL");
            else if(fault=="mismatched")ack.Content=new StringContent(ack.Content.ReadAsStringAsync().Result.Replace(wire.OriginalClientId,"ForeignClient"));
            else ack.Content=new StringContent(ack.Content.ReadAsStringAsync().Result.Replace("\"sCode\":\"0\"","\"sCode\":\""+fault+"\""));
            return Task.FromResult(ack);};
        var d=Dispatcher(f,peer);await d.DispatchAsync("execution-0",cancel.Token);
        Assert.Equal(1,peer.Calls);var i=f.Transactions.Read("execution-0")!;Assert.Equal(NavalExecutionStage.SubmitUnknown,i.Stage);Assert.NotNull(i.DispatchRevision);Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);f.Holds();
        Assert.Equal(NavalMutationStatus.Duplicate,(await d.DispatchAsync("execution-0",default)).Status);Assert.Equal(1,peer.Calls);await d.DisposeAsync();
        string raw=System.IO.File.ReadAllText(f.Target);f.Restart();f.Init();Assert.Equal(raw,System.IO.File.ReadAllText(f.Target));f.Holds();
        var s=f.Owner.Read().Naval!;var receipt=new NavalEvidenceReceipt(f.Coordinator.OwnerToken,f.Definition.Id,"generic-reconcile",f.Generation,1,f.Clock.Now,f.Clock.Now,
            NavalEvidenceKind.Reconcile,true,false,s.Marks,null,s.Accounts,new[]{new NavalObservation(s.Reservations.Single().Id,0,4)},null);
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Reconcile(receipt).Status);
        Assert.Equal(NavalReservationStage.Uncertain,f.Owner.Read().Naval!.Reservations.Single().Stage);
        Assert.Equal(NavalMutationStatus.Refused,f.Coordinator.Start(new(f.Coordinator.OwnerToken,f.Owner.Read().DurableRevision,f.Generation,true)).Status);
        Assert.Equal(wire.CopyBody(),f.Transactions.Read("execution-0")!.Body);Assert.Equal(wire.Deadline,f.Transactions.Read("execution-0")!.Deadline);
        Assert.Equal(NavalMutationStatus.Refused,f.Transactions.ReserveIds(s.Reservations.Single().Id,"fresh-command",new[]{NavalClientIdRole.Parent}).Result.Status);f.Holds();
    }
    [Theory][InlineData("revoke")][InlineData("expiry")][InlineData("sequence")][InlineData("generation")][InlineData("revision")][InlineData("facade")][InlineData("cancel")][InlineData("dispose")]
    public async Task OkxExecution_FinalFenceRejectsChangedContext(string mutation)
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire){Opened=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseOpen=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using var cancellation=new CancellationTokenSource();var d=Dispatcher(f,peer);var pending=d.DispatchAsync("execution-0",cancellation.Token);
        await peer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));Task? disposing=null;
        switch(mutation)
        {
            case "revoke":f.Transactions.OpenOfflineSession().Revoke();break;
            case "expiry":f.Clock.Now=wire.Deadline;break;
            case "revision":Assert.Equal(NavalMutationStatus.Applied,f.Transactions.ReserveIds(f.Owner.Read().Naval!.Reservations.Single().Id,"unrelated",new[]{NavalClientIdRole.Parent}).Result.Status);break;
            case "facade":f.Init();break;
            case "cancel":cancellation.Cancel();break;
            case "dispose":disposing=d.DisposeAsync().AsTask();break;
            default:
                var s=f.Owner.Read().Naval!;long generation=mutation=="generation"?s.Generation+1:s.Generation;long sequence=mutation=="generation"?1:s.Sequence+1;
                var receipt=new NavalEvidenceReceipt(f.Coordinator.OwnerToken,f.Definition.Id,"source-change",generation,sequence,f.Clock.Now,f.Clock.Now,NavalEvidenceKind.Reconcile,true,false,s.Marks,null,s.Accounts,new[]{new NavalObservation(s.Reservations.Single().Id,0,4)},null);
                Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Reconcile(receipt).Status);break;
        }
        peer.ReleaseOpen.TrySetResult(true);await pending.WaitAsync(TimeSpan.FromSeconds(5));if(disposing!=null)await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,peer.Calls);f.Holds();Assert.Equal(wire.CopyBody(),f.Transactions.Read("execution-0")!.Body);await d.DisposeAsync();
    }
    [Fact]
    public async Task OnlyOneDispatcherCanClaimIntentBeforeAsyncWait()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var first=new Peer(f,wire){Opened=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseOpen=new(TaskCreationOptions.RunContinuationsAsynchronously)};using var second=new Peer(f,wire);
        await using var a=Dispatcher(f,first);await using var b=Dispatcher(f,second);
        var pending=a.DispatchAsync("execution-0",default);await first.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateTask=b.DispatchAsync("execution-0",default);bool refusedBeforeWait=duplicateTask.IsCompleted;
        first.ReleaseOpen.SetResult(true);await pending.WaitAsync(TimeSpan.FromSeconds(5));var duplicate=await duplicateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refusedBeforeWait,"Second dispatcher must refuse the in-memory claim before entering adapter waits");
        Assert.Equal(NavalMutationStatus.Duplicate,duplicate.Status);Assert.Equal(1,first.Calls);Assert.Equal(0,second.Calls);f.Holds();
    }
    [Theory][InlineData("sell")][InlineData("protection")][InlineData("cancel-protection")][InlineData("foreign-cancel")][InlineData("size")]
    public async Task UnsupportedWorkflowRefusesWithoutInvocation(string workflow)
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();
        var role=workflow is "protection" or "cancel-protection"?NavalClientIdRole.ProtectionAlgo:NavalClientIdRole.Parent;
        string id=Assert.Single(f.Transactions.ReserveIds(r.ReservationId!,"unsupported",new[]{role}).Ids).ClientId;
        var deadline=f.Clock.Now.AddSeconds(5);
        var wire=workflow switch{
            "protection"=>OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USD","oco","4",id,"130","90",deadline),
            "cancel-protection"=>OkxPreparedRequest.CancelProtection(f.Scope,"BTC-USD",null,id,deadline),
            "foreign-cancel"=>OkxPreparedRequest.Cancel(f.Scope,"BTC-USD","999",id,deadline),
            _=>OkxPreparedRequest.Submit(f.Scope,"BTC-USD",workflow=="sell"?"sell":"buy","fok",workflow=="size"?"999":"4","100",id,deadline)};
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(r.ReservationId!,f.Intent(r,wire,"unsupported")).Status);
        using var peer=new Peer(f,wire);await using var d=Dispatcher(f,peer);
        Assert.Equal("EXECUTION_WORKFLOW_UNSUPPORTED",(await d.DispatchAsync("unsupported",default)).Code);Assert.Equal(0,peer.Calls);Assert.Equal(0,peer.Opens);f.Holds();
    }
    [Fact]
    public async Task OkxExecution_AcknowledgementNeverPostsEconomics_ParentCancel()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using(var peer=new Peer(f,wire))
        {var d=Dispatcher(f,peer);await d.DispatchAsync("execution-0",default);Assert.Equal(1,peer.Calls);f.Holds();
            var parent=f.Transactions.Read("execution-0")!;var reservation=f.Owner.Read().Naval!.Reservations.Single();
            var cancel=OkxPreparedRequest.Cancel(f.Scope,"BTC-USD",parent.Outcome!.OrderId,parent.OriginalClientId,wire.Deadline);
            Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(reservation.Id,f.Intent(new(NavalMutationStatus.Applied,"",reservation.Id,0),cancel,"cancel-0","execution-0")).Status);
            using var cancelPeer=new Peer(f,cancel);await using var cancelDispatcher=Dispatcher(f,cancelPeer);
            Assert.Equal(NavalMutationStatus.Applied,(await cancelDispatcher.DispatchAsync("cancel-0",default)).Status);Assert.Equal(1,cancelPeer.Calls);f.Holds();
            Assert.Equal(NavalExecutionStage.Accepted,f.Transactions.Read("cancel-0")!.Stage);await d.DisposeAsync();}
        f.Restart();f.Init();f.Holds();Assert.All(f.Owner.Read().NavalExecution!.Intents,i=>Assert.Equal(NavalExecutionStage.Accepted,i.Stage));
    }
    [Fact]
    public async Task OkxExecution_InvocationWins_StopWaitsAndRetainsObligation()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire);
        using var stopStarted=new ManualResetEventSlim();Task<NavalRiskResult>? stop=null;
        peer.AtInvocation=()=>{
            var durable=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(System.IO.File.ReadAllText(f.Target),NavalJson.Options)!;
            Assert.Equal(NavalExecutionStage.Dispatching,Assert.Single(durable.NavalExecution!.Intents).Stage);
            stop=Task.Run(()=>{stopStarted.Set();return f.Coordinator.Stop("fixture-stop");});
            Assert.True(stopStarted.Wait(TimeSpan.FromSeconds(5)));Assert.False(stop.IsCompleted,"STOP cannot pass owner lane during synchronous invocation");};
        await using var d=Dispatcher(f,peer);await d.DispatchAsync("execution-0",default);
        Assert.Equal(1,peer.Calls);Assert.NotNull(stop);await stop!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NavalExecutionStage.Accepted,f.Transactions.Read("execution-0")!.Stage);f.Holds();
        long rev=f.Owner.Read().DurableRevision;Assert.Equal(NavalMutationStatus.Duplicate,(await d.DispatchAsync("execution-0",default)).Status);Assert.Equal(rev,f.Owner.Read().DurableRevision);
    }
    [Fact]
    public async Task OkxExecution_StopWinsFinalLane_NoSend()
    {
        using var f=new OkxExecutionFixture();var wire=Prepare(f);using var peer=new Peer(f,wire){Opened=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseOpen=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var d=Dispatcher(f,peer);var pending=d.DispatchAsync("execution-0",default);
        await peer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Coordinator.Stop("fixture-stop");peer.ReleaseOpen.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,peer.Calls);f.Holds();Assert.Equal(NavalExecutionStage.NotSent,f.Transactions.Read("execution-0")!.Stage);
    }
}
