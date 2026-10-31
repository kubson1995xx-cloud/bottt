using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Desktop.Services;
using System.Diagnostics;
using System.Threading;
using Apex.Core.Naval;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;
public sealed class OkxExecutionDurabilityTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public OkxExecutionDurabilityTests(Xunit.Abstractions.ITestOutputHelper output){_output=output;}
    [Theory] [InlineData("AfterTempFlush",false)] [InlineData("AfterReplace",false)] [InlineData("AfterTempFlush",true)] [InlineData("AfterReplace",true)]
    public async Task OkxExecution_OwnedProcessCrashReopensActualTarget(string boundary,bool idCommit)
    {
        using var f=new OkxExecutionFixture();f.Run();var reservation=f.Reserve();f.Init();
        if(!idCommit)Assert.Equal(NavalMutationStatus.Applied,f.Transactions.ReserveIds(reservation.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent}).Result.Status);
        f.Owner.Dispose();string barrier=f.DirectoryPath+"-barrier";
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{typeof(SyntheticHostEntry).Assembly.Location,"--okx-p-crash",f.DirectoryPath,barrier,boundary,idCommit?"id":"prepare"})start.ArgumentList.Add(arg);
        start.Environment["APEX_OKX_PUBLIC_READ_SMOKE"]="0";
        _output.WriteLine("Owned crash fixture command: "+start.FileName+" "+string.Join(" ",start.ArgumentList));
        using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(!File.Exists(barrier))
            {Assert.False(child.HasExited,"Fixture exited before checkpoint");await Task.Delay(10,timeout.Token);}
            Assert.Equal("READY "+boundary,File.ReadAllText(barrier));Assert.False(child.HasExited);
            child.Kill(entireProcessTree:false);await child.WaitForExitAsync(timeout.Token);Assert.NotEqual(0,child.ExitCode);
            _output.WriteLine("Killed/reaped owned pid "+child.Id+" exit "+child.ExitCode+" at "+boundary);
            Assert.Empty(await stdout);Assert.Empty(await stderr);
            string raw=File.ReadAllText(f.Target);var before=System.Text.Json.JsonSerializer.Deserialize<EngineState>(File.ReadAllText(barrier+".before.json"),NavalJson.Options)!;
            var marker=File.ReadAllBytes(Path.Combine(f.DirectoryPath,".naval-initialized"));
            var candidates=Directory.GetFiles(f.DirectoryPath,"*.tmp").ToDictionary(p=>p,p=>File.ReadAllBytes(p));
            Assert.Equal(boundary=="AfterTempFlush"?1:0,candidates.Count);
            f.Open();f.Init();f.Holds();var e=f.Owner.Read();bool landed=boundary=="AfterReplace";
            Assert.Equal(raw,File.ReadAllText(f.Target));Assert.Equal(marker,File.ReadAllBytes(Path.Combine(f.DirectoryPath,".naval-initialized")));
            foreach(var p in candidates)Assert.Equal(p.Value,File.ReadAllBytes(p.Key));
            Assert.Equal(before.DurableRevision+(landed?1:0),e.DurableRevision);Assert.Equal(e.DurableRevision,e.NavalExecution!.FinancialRevision);
            Assert.Equal(e.DurableRevision,e.NavalReadJournal!.Checkpoint.FinancialRevision);Assert.Equal(before.NavalReadJournal!.Epoch,e.NavalReadJournal.Epoch);
            Assert.Equal(landed?1:0,idCommit?e.NavalExecution.IdAllocations.Count:e.NavalExecution.Intents.Count);
            if(!idCommit)
            {
                Assert.Equal(NavalJson.Serialize(before.NavalExecution!.IdAllocations),NavalJson.Serialize(e.NavalExecution.IdAllocations));
                if(landed)
                {var i=Assert.Single(e.NavalExecution.Intents);var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",i.OriginalClientId,new(2026,1,1,12,0,5,TimeSpan.Zero));
                    Assert.Equal(wire.CopyBody(),i.Body);Assert.Equal(wire.Digest,i.RequestDigest);Assert.Equal(NavalExecutionStage.Prepared,i.Stage);}
            }
            Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);Assert.Equal("UNKNOWN",f.Coordinator.Snapshot().Quality);Assert.Empty(e.Naval!.Fills);
            _output.WriteLine("Actual target revision "+e.DurableRevision+"; candidates preserved "+candidates.Count+"; no peer/transport exists");
        }
        finally
        {if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}File.Delete(barrier);File.Delete(barrier+".before.json");}
    }

    // Break caught: durable IDs or prepared bytes lost while financial holds already committed.
    [Fact] public void OkxExecution_PreparedReopensExactBytesAndOriginalHolds()
    {
        using var f=new OkxExecutionFixture();f.Run();var reservation=f.Reserve();
        Assert.Equal(NavalMutationStatus.Applied,reservation.Status);f.Holds();
        f.Init();var ids=f.Transactions.ReserveIds(reservation.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo});
        Assert.Equal(NavalMutationStatus.Applied,ids.Result.Status);Assert.Equal(2,ids.Ids.Count);Assert.NotEqual(ids.Ids[0].ClientId,ids.Ids[1].ClientId);
        var deadline=new DateTimeOffset(2026,1,1,12,0,5,TimeSpan.Zero);
        var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids.Ids[0].ClientId,deadline,new(ids.Ids[1].ClientId,"130","90"));
        var input=f.Intent(reservation,wire);var originalBody=wire.CopyBody();
        int receipts=f.Owner.Read().Naval!.Receipts.Count;
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(reservation.ReservationId!,input).Status);
        input.Body[0]=0;f.Holds();f.Restart();f.Init();
        var restored=Assert.IsType<NavalExecutionIntent>(f.Transactions.Read("execution-0"));
        Assert.Equal(originalBody,restored.Body);Assert.Equal(wire.Digest,restored.RequestDigest);Assert.Equal(deadline,restored.Deadline);
        Assert.Equal("POST",restored.Method);Assert.Equal("/api/v5/trade/order",restored.Path);Assert.Equal(f.Binding,restored.Binding);
        Assert.Equal("financial-0",restored.CommandId);Assert.Equal("s-0",restored.SignalId);Assert.Null(restored.DispatchRevision);Assert.Null(restored.Outcome);
        Assert.Equal(NavalExecutionStage.Prepared,restored.Stage);Assert.Equal(receipts,f.Owner.Read().Naval!.Receipts.Count);
        var replay=OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Submit,restored.Instrument,restored.OriginalClientId,restored.OrderId,restored.Deadline,restored.Body);
        Assert.Equal(wire.Digest,replay.Digest);Assert.Equal(originalBody,replay.CopyBody());f.Holds();
        Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);Assert.Equal("UNKNOWN",f.Coordinator.Snapshot().Quality);
        var publication=f.Coordinator.ReadPublication()!;Assert.Empty(publication.Journal.Batches.Last().Facts);
        Assert.Equal(restored.PreparedRevision,publication.FinancialRevision);
    }
    [Fact] public async Task OkxExecution_IdAllocationIsPermanentAndIdempotent()
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();var roles=new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo};
        var first=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",roles);Assert.Equal(NavalMutationStatus.Applied,first.Result.Status);
        long revision=f.Owner.Read().DurableRevision;
        var duplicate=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",roles);Assert.Equal(NavalMutationStatus.Duplicate,duplicate.Result.Status);
        Assert.Equal(first.Ids,duplicate.Ids);Assert.Equal(revision,f.Owner.Read().DurableRevision);
        var concurrent=await Task.WhenAll(Enumerable.Range(1,8).Select(n=>Task.Run(()=>f.Transactions.ReserveIds(r.ReservationId!,"execution-"+n,roles))));
        Assert.All(concurrent,x=>Assert.Equal(NavalMutationStatus.Applied,x.Result.Status));
        var ids=f.Owner.Read().NavalExecution!.IdAllocations.SelectMany(a=>a.Ids).ToArray();Assert.Equal(18,ids.Length);Assert.Equal(18,ids.Select(i=>i.ClientId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1,18).Select(n=>(ulong)n),ids.Select(i=>i.Counter));
        // Existing Offline final zero-fill settlement remains the sole financial finality owner.
        var s=f.Owner.Read().Naval!;var terminal=new NavalEvidenceReceipt(f.Coordinator.OwnerToken,f.Definition.Id,"terminal",1,s.Sequence+1,f.Clock.Now,f.Clock.Now,
            NavalEvidenceKind.Reject,true,true,s.Marks,null,s.Accounts,new[]{new NavalObservation(r.ReservationId!,0,0)},new[]{r.ReservationId!});
        Assert.Equal(NavalMutationStatus.Applied,f.Coordinator.Reject(terminal).Status);Assert.Equal(0m,f.Coordinator.Snapshot().Held);
        for(int n=0;n<514;n++)Assert.Equal(NavalMutationStatus.Stopped,f.Coordinator.Stop("maintenance").Status);
        Assert.Equal(512,f.Coordinator.ReadPublication()!.Journal.Batches.Count);
        f.Restart();f.Init();var replay=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",roles);
        Assert.Equal(NavalMutationStatus.Duplicate,replay.Result.Status);Assert.Equal(first.Ids,replay.Ids);
        Assert.Equal(NavalMutationStatus.Duplicate,f.Coordinator.ForFleet(f.Account,NavalFleet.Alpha).TryReserve(f.FinancialIntent()).Status);
        f.Run();var consumed=f.Coordinator.ForFleet(f.Account,NavalFleet.Alpha).TryReserve(f.FinancialIntent("fresh-command"));Assert.Equal("SIGNAL_CONSUMED",consumed.Code);
        Assert.Single(f.Owner.Read().Naval!.Reservations);
        var conflict=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent});
        Assert.Equal(NavalMutationStatus.Stopped,conflict.Result.Status);Assert.Empty(conflict.Ids);Assert.Equal(18UL,f.Owner.Read().NavalExecution!.HighWater);
        Assert.Equal("STOP",f.Coordinator.Snapshot().Mode);
    }
    [Fact] public void OkxExecution_CheckedExhaustionArithmeticAndRealIdControl()
    {
        Assert.Equal(ulong.MaxValue,NavalExecutionValidation.CheckedAllocationEnd(ulong.MaxValue-2,2));
        Assert.Throws<OverflowException>(()=>NavalExecutionValidation.CheckedAllocationEnd(ulong.MaxValue,1));
        Assert.Throws<OverflowException>(()=>NavalExecutionValidation.CheckedAllocationEnd(ulong.MaxValue-1,2));
        Assert.Equal("0123456789ABCDEFFFFFFFFFFFFFFFFF",NavalExecutionValidation.ClientId("0123456789ABCDEF",ulong.MaxValue));
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();
        var tx=NavalExecutionTransactions.ForOfflineTests(f.Coordinator,f.Binding,"0123456789ABCDEF");
        var allocation=tx.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent});Assert.Equal(NavalMutationStatus.Applied,allocation.Result.Status);
        Assert.Equal("0123456789ABCDEF0000000000000001",Assert.Single(allocation.Ids).ClientId);
        f.Restart();f.Init();Assert.Equal(allocation.Ids,f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent}).Ids);
    }
    [Theory] [InlineData("binding")] [InlineData("namespace")] [InlineData("counter")] [InlineData("gap")] [InlineData("drop")]
    [InlineData("bytes")] [InlineData("deadline")] [InlineData("session")] [InlineData("tombstone")] [InlineData("stop-epoch")]
    public void OkxExecution_RehashedTransitionCannotRewriteHistory(string mutation)
    {
        using var f=new OkxExecutionFixture();OkxExecutionCompatibilityTests.Prepared(f);var old=f.Owner.Read();var e=NavalJson.Clone(old);var x=e.NavalExecution!;
        switch(mutation)
        {
            case "binding":x=x with{Binding=x.Binding with{ScopeId="different"}};break;
            case "namespace":x=x with{Namespace="FFFFFFFFFFFFFFFF"};break;
            case "counter":x=x with{HighWater=1};break;
            case "gap":x=x with{IdAllocations=Array.Empty<NavalExecutionIdAllocation>(),HighWater=0};break;
            case "drop":x=null!;break;
            case "bytes":x=x with{Intents=new[]{x.Intents[0] with{Body=new byte[]{123,125}}}};break;
            case "deadline":x=x with{Intents=new[]{x.Intents[0] with{Deadline=f.Clock.Now.AddHours(1)}}};break;
            case "session":x=x with{Intents=new[]{x.Intents[0] with{PreparationSessionId=Guid.NewGuid().ToString("N")}}};break;
            case "tombstone":e.Naval=e.Naval! with{Commands=Array.Empty<NavalCommand>()};break;
            case "stop-epoch":x=x with{StopEpoch=1};break;
        }
        e.NavalExecution=x;e.DurableRevision++;e.Naval=e.Naval! with{Revision=e.DurableRevision};
        e.NavalReadJournal=NavalReadJournal.Append(old,e,new(NavalPublicationOperation.ExecutionMaintenance,"",Guid.NewGuid().ToString("N")),f.Clock.Now,f.Definition);
        string disk=File.ReadAllText(f.Target);string publication=NavalJson.Serialize(f.Coordinator.ReadPublication());
        if(mutation=="stop-epoch")
        {
            Assert.Equal(NavalCommitResult.Committed,f.Owner.TryCommit(old.DurableRevision,e));var advanced=f.Owner.Read();var rewind=NavalJson.Clone(advanced);
            rewind.DurableRevision++;rewind.Naval=rewind.Naval! with{Revision=rewind.DurableRevision};rewind.NavalExecution=rewind.NavalExecution! with{StopEpoch=0};
            rewind.NavalReadJournal=NavalReadJournal.Append(advanced,rewind,new(NavalPublicationOperation.ExecutionMaintenance,"",Guid.NewGuid().ToString("N")),f.Clock.Now,f.Definition);
            Assert.Equal(NavalCommitResult.Faulted,f.Owner.TryCommit(advanced.DurableRevision,rewind));Assert.Equal(1UL,f.Owner.Read().NavalExecution!.StopEpoch);return;
        }
        Assert.Equal(NavalCommitResult.Faulted,f.Owner.TryCommit(old.DurableRevision,e));
        Assert.Equal(disk,File.ReadAllText(f.Target));Assert.Equal(publication,NavalJson.Serialize(f.Coordinator.ReadPublication()));f.Holds();
    }
    [Fact] public void OkxExecution_GoldenCanonicalDigestAgreesAcrossAssemblies()
    {
        using var f=new OkxExecutionFixture();const string id="0123456789ABCDEF0000000000000001";
        var wire=OkxPreparedRequest.Cancel(f.Scope,"BTC-USD","123",id,new(2026,1,1,12,0,5,TimeSpan.Zero));
        const string body="{\"instId\":\"BTC-USD\",\"ordId\":\"123\",\"clOrdId\":\"0123456789ABCDEF0000000000000001\"}";
        const string digest="3b4edf6c43844bb9029bf7b00467ec0226e068c2b2f1234f2a1195e697ab1add";
        Assert.Equal(body,System.Text.Encoding.UTF8.GetString(wire.CopyBody()));Assert.Equal(digest,wire.Digest);
        var intent=new NavalExecutionIntent{Binding=f.Binding,Method="POST",Path="/api/v5/trade/cancel-order",Instrument="BTC-USD",OriginalClientId=id,OrderId="123",Deadline=wire.Deadline,Body=System.Text.Encoding.UTF8.GetBytes(body)};
        Assert.Equal(digest,NavalExecutionValidation.RequestDigest(intent));
        Assert.Equal(digest,OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Cancel,"BTC-USD",id,"123",wire.Deadline,intent.Body).Digest);
    }
    [Fact] public void OkxExecution_AllCanonicalOperationsPersistWithoutDispatch()
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();
        var parent=f.Transactions.ReserveIds(r.ReservationId!,"submit",new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo}).Ids;
        var standalone=f.Transactions.ReserveIds(r.ReservationId!,"protect",new[]{NavalClientIdRole.ProtectionAlgo}).Ids;
        var deadline=f.Clock.Now.AddSeconds(5);
        var requests=new[]{("submit",OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",parent[0].ClientId,deadline,new(parent[1].ClientId,"130","90")),"submit"),
            ("cancel",OkxPreparedRequest.Cancel(f.Scope,"BTC-USD","123",parent[0].ClientId,deadline),"submit"),
            ("protect",OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USD","oco","4",standalone[0].ClientId,"130","90",deadline),"protect"),
            ("cancel-protect",OkxPreparedRequest.CancelProtection(f.Scope,"BTC-USD",null,standalone[0].ClientId,deadline),"protect")};
        foreach(var (id,wire,key) in requests)
        {var input=f.Intent(r,wire,id,key);Assert.Equal(wire.Digest,NavalExecutionValidation.RequestDigest(input));
            Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(r.ReservationId!,input).Status);}
        f.Restart();f.Init();foreach(var (id,wire,key) in requests)
        {var i=f.Transactions.Read(id)!;var restored=OkxPreparedRequest.Restore(f.Scope,wire.Operation,i.Instrument,i.OriginalClientId,i.OrderId,i.Deadline,i.Body);
            Assert.Equal(wire.CopyBody(),restored.CopyBody());Assert.Equal(wire.Digest,restored.Digest);}
        f.Holds();
    }
    [Theory]
    [InlineData("BeforeTempCreate",false)] [InlineData("AfterTempWrite",false)] [InlineData("AfterTempFlush",false)] [InlineData("BeforeReplace",false)] [InlineData("AfterReplace",false)] [InlineData("BeforeSet",false)] [InlineData("OwnerFlush",false)]
    [InlineData("BeforeTempCreate",true)] [InlineData("AfterTempWrite",true)] [InlineData("AfterTempFlush",true)] [InlineData("BeforeReplace",true)] [InlineData("AfterReplace",true)] [InlineData("BeforeSet",true)] [InlineData("OwnerFlush",true)]
    public void OkxExecution_PreparationDiskFaultMatrix(string boundary,bool idCommit)
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();
        if(!idCommit)Assert.Equal(NavalMutationStatus.Applied,f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent}).Result.Status);
        f.Owner.Dispose();bool armed=false;int observed=0;
        var real=new FileStore(f.DirectoryPath,b=>{if(armed&&b.ToString()==boundary){observed++;throw new IOException("synthetic boundary");}});
        var wrapper=new OwnerFaultStore(real,()=>armed,boundary);
        f.Open(wrapper);f.Generation++;f.Run();f.Init();
        var old=f.Owner.Read();string publication=NavalJson.Serialize(f.Coordinator.ReadPublication());
        NavalExecutionIntent? intent=null;
        if(!idCommit)
        {var id=old.NavalExecution!.IdAllocations.Single().Ids.Single().ClientId;intent=f.Intent(r,OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",id,f.Clock.Now.AddSeconds(5)));}
        armed=true;NavalRiskResult result;
        if(idCommit){var ids=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent});result=ids.Result;Assert.Empty(ids.Ids);}
        else result=f.Transactions.Prepare(r.ReservationId!,intent!);
        Assert.Equal(NavalMutationStatus.Stopped,result.Status);
        if(boundary is not ("BeforeSet" or "OwnerFlush"))Assert.Equal(1,observed);
        Assert.Equal(NavalJson.Serialize(old),NavalJson.Serialize(f.Owner.Read()));Assert.Equal(publication,NavalJson.Serialize(f.Coordinator.ReadPublication()));f.Holds();
        string actual=File.ReadAllText(f.Target);var inventory=Directory.GetFiles(f.DirectoryPath).OrderBy(p=>p).ToArray();
        Assert.Equal(NavalMutationStatus.Stopped,f.Transactions.ReserveIds(r.ReservationId!,"after-freeze",new[]{NavalClientIdRole.Parent}).Result.Status);
        Assert.Equal(actual,File.ReadAllText(f.Target));Assert.Equal(inventory,Directory.GetFiles(f.DirectoryPath).OrderBy(p=>p));
        bool landed=boundary is "AfterReplace" or "OwnerFlush";f.Restart();f.Init();f.Holds();var reopened=f.Owner.Read();
        Assert.Equal(old.DurableRevision+(landed?1:0),reopened.DurableRevision);Assert.Equal(reopened.DurableRevision,reopened.NavalExecution!.FinancialRevision);
        Assert.Equal(reopened.DurableRevision,reopened.NavalReadJournal!.Checkpoint.FinancialRevision);
        Assert.Equal(landed?1:0,idCommit?reopened.NavalExecution.IdAllocations.Count:reopened.NavalExecution.Intents.Count);
        if(landed)Assert.Empty(reopened.NavalReadJournal.Batches.Last().Facts);
        else Assert.Equal(NavalJson.Serialize(old.NavalReadJournal!.Batches),NavalJson.Serialize(reopened.NavalReadJournal.Batches));
        Assert.Empty(reopened.Naval!.Fills);
        Assert.Equal(old.NavalReadJournal!.Epoch,reopened.NavalReadJournal.Epoch);
        Assert.Equal(landed?old.NavalReadJournal.LastSequence+1:old.NavalReadJournal.LastSequence,reopened.NavalReadJournal.LastSequence);
        if(landed&&!idCommit)Assert.Equal(intent!.Body,Assert.Single(reopened.NavalExecution.Intents).Body);
        Assert.Empty(Directory.GetFiles(f.DirectoryPath,"*.tmp"));
    }
    private sealed class OwnerFaultStore : IKeyValueStore
    {
        private readonly FileStore _inner;private readonly Func<bool> _armed;private readonly string _boundary;
        internal OwnerFaultStore(FileStore inner,Func<bool> armed,string boundary){_inner=inner;_armed=armed;_boundary=boundary;}
        public string? Get(string key)=>_inner.Get(key);
        public void Set(string key,string value){if(_armed()&&_boundary=="BeforeSet")throw new IOException("before owner Set");_inner.Set(key,value);}
        public void Flush(){if(_armed()&&_boundary=="OwnerFlush")throw new IOException("owner Flush (not physical fsync)");_inner.Flush();}
    }
    [Fact] public void OkxExecution_ActualTaskOneConfigBindingIsAcceptedAsOfflineComparisonData()
    {
        using var f=new OkxExecutionFixture();f.Run();Assert.Equal(NavalMutationStatus.Applied,f.Reserve().Status);
        var configured=Apex.Desktop.Okx.OkxConfiguration.Bind(new Apex.Desktop.Okx.OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new Apex.Desktop.Okx.OkxScopeOptions{
            ScopeId="fixture-scope",Environment="OkxDemo",Region="EEA",ExpectedUid="123456",ExpectedMainUid="123456",QuoteCurrency="USD",CredentialBindingId="fixture-key",SafetyVersion="okx-cash-v1"}}}).Single();
        NavalExecutionTransactions? tx=null;var error=Record.Exception(()=>tx=NavalExecutionTransactions.ForOfflineTests(f.Coordinator,f.Binding with{ConfigDigest=configured.ConfigDigest}));
        Assert.Null(error);Assert.NotNull(tx);Assert.Equal(NavalEnvironment.Offline,f.Owner.Read().NavalExecution!.Binding.Account.Environment);
    }
    [Fact] public void OkxExecution_AttachmentIdentityCannotBePlacedAgain()
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();
        string parent=f.Transactions.ReserveIds(r.ReservationId!,"submit",new[]{NavalClientIdRole.Parent}).Ids.Single().ClientId;
        string algo=f.Transactions.ReserveIds(r.ReservationId!,"algo",new[]{NavalClientIdRole.ProtectionAlgo}).Ids.Single().ClientId;
        var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",parent,f.Clock.Now.AddSeconds(5),new(algo,"130","90"));
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(r.ReservationId!,f.Intent(r,wire,"submit","submit","algo")).Status);
        long revision=f.Owner.Read().DurableRevision;
        var again=OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USD","oco","4",algo,"130","90",f.Clock.Now.AddSeconds(5));
        Assert.Equal(NavalMutationStatus.Refused,f.Transactions.Prepare(r.ReservationId!,f.Intent(r,again,"algo","algo")).Status);
        Assert.Equal(revision,f.Owner.Read().DurableRevision);Assert.Single(f.Owner.Read().NavalExecution!.Intents);f.Holds();
    }
    [Fact] public void OkxExecution_InitializationFaultNeverReturnsFacade()
    {
        using var f=new OkxExecutionFixture();f.Run();Assert.Equal(NavalMutationStatus.Applied,f.Reserve().Status);f.Owner.Dispose();
        f.Open(new OwnerFaultStore(new FileStore(f.DirectoryPath),()=>true,"OwnerFlush"));
        long revision=f.Owner.Read().DurableRevision;string publication=NavalJson.Serialize(f.Coordinator.ReadPublication());
        Assert.Throws<InvalidDataException>(()=>f.Init());Assert.Null(f.Transactions);Assert.Null(f.Owner.Read().NavalExecution);
        Assert.Equal(revision,f.Owner.Read().DurableRevision);Assert.Equal(publication,NavalJson.Serialize(f.Coordinator.ReadPublication()));f.Holds();
        Assert.Throws<InvalidDataException>(()=>f.Init());f.Restart();f.Init();Assert.NotNull(f.Owner.Read().NavalExecution);f.Holds();
    }
    [Fact] public void OkxExecution_BeforeFinancialReserveFaultReturnsNoReservationOrIdentity()
    {
        using var f=new OkxExecutionFixture();f.Run();f.Init();f.Owner.Dispose();bool armed=false;
        f.Open(new OwnerFaultStore(new FileStore(f.DirectoryPath),()=>armed,"BeforeSet"));f.Generation++;f.Run();f.Init();var old=f.Owner.Read();
        armed=true;var r=f.Reserve();Assert.Equal(NavalMutationStatus.Stopped,r.Status);Assert.Null(r.ReservationId);Assert.Null(f.Owner.Read().Naval!.Reservations.SingleOrDefault());
        var ids=f.Transactions.ReserveIds(r.ReservationId??"absent","failed",new[]{NavalClientIdRole.Parent});Assert.Empty(ids.Ids);Assert.Equal(NavalMutationStatus.Stopped,ids.Result.Status);
        Assert.Equal(NavalJson.Serialize(old),NavalJson.Serialize(f.Owner.Read()));f.Restart();Assert.Empty(f.Owner.Read().Naval!.Reservations);Assert.Empty(f.Owner.Read().NavalExecution!.IdAllocations);
    }
    [Fact] public void OkxExecution_PreparationCapturesStampsAndConflictsStop()
    {
        using var f=new OkxExecutionFixture();f.Run();var r=f.Reserve();f.Init();var ids=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent}).Ids;
        var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids[0].ClientId,f.Clock.Now.AddSeconds(5));var input=f.Intent(r,wire);
        foreach(var mutant in new[]{input with{SourceGeneration=2},input with{SourceSequence=0},input with{PreparedRevision=1},input with{DefinitionDigest="wrong"},
            input with{CatalogDigest="wrong"},input with{ReservationDigest="wrong"},input with{PreparationSessionId=Guid.NewGuid().ToString("N")},input with{StopEpoch=1},input with{Deadline=f.Clock.Now}})
            Assert.Equal(NavalMutationStatus.Refused,f.Transactions.Prepare(r.ReservationId!,mutant).Status);
        Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(r.ReservationId!,input).Status);
        long revision=f.Owner.Read().DurableRevision;Assert.Equal(NavalMutationStatus.Duplicate,f.Transactions.Prepare(r.ReservationId!,input).Status);Assert.Equal(revision,f.Owner.Read().DurableRevision);
        var detached=f.Transactions.Read("execution-0")!;var body=detached.Body;body[0]=0;Assert.Equal(wire.CopyBody(),f.Transactions.Read("execution-0")!.Body);
        Assert.Equal(NavalMutationStatus.Stopped,f.Transactions.Prepare(r.ReservationId!,input with{Deadline=input.Deadline.AddSeconds(1)}).Status);f.Holds();
        Assert.Equal(NavalMutationStatus.Refused,f.Transactions.ReserveIds(r.ReservationId!,"after-stop",new[]{NavalClientIdRole.Parent}).Result.Status);
    }
}
