using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Windows.Tests;

[Collection("Extracted Windows")]
public sealed class WindowsOkxCommitIntegrationTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root=WindowsTestHost.NewRoot();
        internal readonly byte[][] Values=Enumerable.Range(0,3).Select(i=>Encoding.ASCII.GetBytes("owned-public-synthetic-not-broker-field-"+i)).ToArray();
        internal readonly ConfigStore Config;
        internal readonly WindowsOkxReadStore Store;
        internal readonly OkxEnrollmentObservation Observation;
        internal Fixture(){using(var admitted=WindowsPrivateData.Open(Root,AppContext.BaseDirectory)){}Config=new(Root);Store=new(Root);Observation=Observed(Values);}
        internal static OkxEnrollmentObservation Observed(byte[][] values)=>new("123456","123456","Demo","DemoTradingApi",Convert.ToHexString(SHA256.HashData(values[0])).ToLowerInvariant(),DateTimeOffset.UtcNow,ImmutableArray<OkxExecutionBalanceDetailDto>.Empty,ImmutableArray<OkxExecutionInstrumentDto>.Empty);
        public void Dispose(){foreach(var v in Values)CryptographicOperations.ZeroMemory(v);Directory.Delete(Root,true);}
    }
    private static object Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    [Theory,Trait("Gate","CommitFence")]
    [InlineData("stop")][InlineData("emergency")][InlineData("cancel")][InlineData("disconnect")][InlineData("lifetime")]
    public async Task Actual_controller_revocation_serializes_with_prepared_publication(string action)
    {
        using var f=new Fixture();await using var runtime=OkxNavalRuntime.Compose(f.Root,OkxConfiguration.Bind(f.Config.Config.OkxNaval));await using var control=new OkxOperatorControl(runtime,f.Config);
        object sync=Field(control,"sync");long epoch=(long)Field(control,"generation");using var stop=new CancellationTokenSource();
        typeof(OkxOperatorControl).GetField("pending",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(control,stop);
        var accounts=(ConcurrentDictionary<string,OkxReadAccount>)Field(control,"readAccounts");
        using var commit=new OkxReadCommit(sync,()=>(long)Field(control,"generation")==epoch,stop.Token,f.Observation,f.Values,a=>accounts.TryAdd(a.ScopeId,a));commit.Approve(commit.Request);
        bool reached=false;
        try{f.Store.Save(f.Observation,f.Values,commit,_=>{reached=true;if(action=="lifetime")control.DisposeAsync().AsTask().GetAwaiter().GetResult();else control.ExecuteAsync("pending",action,Guid.NewGuid().ToString("N"),default).GetAwaiter().GetResult();});}catch(OperationCanceledException){}catch(InvalidDataException){}
        typeof(OkxOperatorControl).GetField("pending",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(control,null);
        Assert.True(reached);Assert.Null(commit.Account);Assert.Empty(f.Store.List());Assert.Empty(accounts);Assert.Empty(Directory.GetFiles(f.Root,"*.pending",SearchOption.AllDirectories));
    }
    [Theory,Trait("Gate","CommitFence")]
    [InlineData("USER_CANCELLED")][InlineData("CANCELLED")][InlineData("TIMEOUT")]
    public async Task Commit_winner_is_immediately_listed_and_canceled_form_reports_saved_disconnected(string formCode)
    {
        using var f=new Fixture();await using var runtime=OkxNavalRuntime.Compose(f.Root,OkxConfiguration.Bind(f.Config.Config.OkxNaval));await using var control=new OkxOperatorControl(runtime,f.Config);
        object sync=Field(control,"sync");using var stop=new CancellationTokenSource();var accounts=(ConcurrentDictionary<string,OkxReadAccount>)Field(control,"readAccounts");
        using var commit=new OkxReadCommit(sync,()=>true,stop.Token,f.Observation,f.Values,a=>{accounts.TryAdd(a.ScopeId,a);stop.Cancel();});commit.Approve(commit.Request);
        var account=f.Store.Save(f.Observation,f.Values,commit,null);Assert.True(stop.IsCancellationRequested);Assert.Equal(account,commit.Account);
        var result=WindowsOkxSimpleConnect.Complete(new(account,f.Observation,"CONNECTED_READ_ONLY_STOP"),formCode);
        Assert.Equal(account,result.Account);Assert.Null(result.Observation);Assert.Equal("CONNECT_CANCELLED_SAVED_DISCONNECTED",result.Code);
        string status=JsonSerializer.Serialize(control.ReadStatus());Assert.Contains("SAVED_DISCONNECTED_RECONNECT_REQUIRED",status);Assert.Contains("\"armed\":false",status);Assert.Contains("\"connected\":false",status);Assert.Single(f.Store.List());Assert.Empty(runtime.Bindings);
    }
    [Theory,Trait("Gate","CommitFence")]
    [InlineData(false)][InlineData(true)]
    public void Missing_or_wrong_request_approval_cannot_prepare_or_commit(bool wrongRequest)
    {
        using var f=new Fixture();using var commit=new OkxReadCommit(new object(),()=>true,default,f.Observation,f.Values,_=>Assert.Fail("Unapproved saved"));
        if(wrongRequest)Assert.Throws<InvalidDataException>(()=>commit.Approve(Guid.Empty));
        Assert.Throws<InvalidDataException>(()=>f.Store.Save(f.Observation,f.Values,commit,_=>Assert.Fail("Unapproved prepared")));Assert.Empty(f.Store.List());
    }
    [Fact,Trait("Gate","CommitFence")]
    public void Existing_final_and_unexplained_pending_are_not_overwritten_deleted_or_promoted()
    {
        using var f=new Fixture();byte[] config=File.ReadAllBytes(f.Config.Path_);string? existing=null;byte[] marker={17,29,41};
        using(var commit=new OkxReadCommit(new object(),()=>true,default,f.Observation,f.Values,_=>Assert.Fail("Conflicting save listed")))
        {
            commit.Approve(commit.Request);
            Assert.Throws<InvalidDataException>(()=>f.Store.Save(f.Observation,f.Values,commit,_=>{var stage=Assert.Single(Directory.GetFiles(Path.Combine(f.Root,"okx-read-pending")));existing=Path.Combine(f.Root,"okx-read",Path.GetFileName(stage)[..^8]);File.WriteAllBytes(existing,marker);}));
        }
        Assert.NotNull(existing);Assert.True(marker.AsSpan().SequenceEqual(File.ReadAllBytes(existing)),"Conflicting final changed");Assert.Empty(Directory.GetFiles(Path.Combine(f.Root,"okx-read-pending")));
        string unexplained=Path.Combine(f.Root,"okx-read-pending","unexplained.pending");File.WriteAllBytes(unexplained,marker);
        var inventory=WindowsTestHost.Inventory(f.Root);Assert.Throws<InvalidDataException>(()=>f.Store.List());Assert.Throws<InvalidDataException>(()=>f.Store.Save(f.Observation,f.Values));WindowsTestHost.SameInventory(inventory,f.Root);Assert.True(config.AsSpan().SequenceEqual(File.ReadAllBytes(f.Config.Path_)),"Old config changed");
    }
    [Theory,Trait("Gate","CommitFence")]
    [InlineData("native-anuluj")][InlineData("lifetime-token")]
    public async Task Native_form_cancellation_reaches_the_actual_prepared_store_fence(string action)
    {
        using var f=new Fixture();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var prepared=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool canceled=false;byte[][]? captured=null;
        var form=WindowsOkxSimpleConnect.FormAsync(stop.Token,(values,operation)=>
        {
            captured=values;var observed=Fixture.Observed(values);using var commit=new OkxReadCommit(new object(),()=>true,operation,observed,values,_=>Assert.Fail("Canceled native submit enrolled"));commit.Approve(commit.Request);
            try{f.Store.Save(observed,values,commit,_=>{prepared.SetResult();Assert.True(operation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),"Native cancellation did not reach prepared operation");});}
            catch(OperationCanceledException){canceled=true;}
            return Task.FromResult<string?>("CONNECT_CANCELLED");
        });
        try
        {
            var w=await WindowsNativeUi.Window(Environment.ProcessId,"APEX — lokalny klucz OKX");for(int i=100;i<103;i++)WindowsNativeUi.Text(w,i,"owned-public-synthetic-not-broker-field-"+i);WindowsNativeUi.Click(w,1);await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(action=="native-anuluj")WindowsNativeUi.Cancel(w);else stop.Cancel();
            var result=await form.WaitAsync(TimeSpan.FromSeconds(5));Assert.Null(result.Values);Assert.True(canceled);Assert.Empty(f.Store.List());Assert.Empty(Directory.GetFiles(f.Root,"*.pending",SearchOption.AllDirectories));Assert.NotNull(captured);Assert.True(captured.All(v=>v.All(b=>b==0)),"Owned form bytes not cleared; withheld");
            WindowsTestHost.Evidence("native-commit-"+action,new{reachedPreparedProductionStore=true,canceled=true,enrolled=false,secureApprovalInjected=true,fullSecurePositive=false,networkCalled=false});
        }
        finally{stop.Cancel();await form;}
    }
}
