using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Windows.Tests;

[Collection("Extracted Windows")]
public sealed class WindowsOkxCommitFenceTests
{
    // This adapter deliberately supports the original production DLL: it replays
    // its check -> progress -> actual Save -> late cancellation ordering. The
    // corrected branch invokes the actual production fence/store, not a model.
    private sealed class Attempt : IDisposable
    {
        internal readonly string Root=WindowsTestHost.NewRoot();
        internal readonly byte[][] Values=Enumerable.Range(0,3).Select(i=>Encoding.ASCII.GetBytes("owned-public-synthetic-not-broker-field-"+i)).ToArray();
        internal readonly CancellationTokenSource Stop=new();
        internal readonly object Sync=new();
        internal readonly WindowsOkxReadStore Store;
        internal readonly OkxEnrollmentObservation Observation;
        internal readonly ConfigStore Config;
        internal readonly byte[] OriginalConfig;
        internal bool Current=true;
        internal DateTimeOffset Utc=new(2026,11,1,0,0,0,TimeSpan.Zero);
        internal long Ticks=1000;
        internal object? Fence;
        internal OkxReadAccount? Listed;
        private static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
        internal Attempt()
        {
            using(var admitted=WindowsPrivateData.Open(Root,AppContext.BaseDirectory)){}
            Config=new(Root);OriginalConfig=File.ReadAllBytes(Config.Path_);Store=new(Root);
            Observation=new("123456","123456","Demo","DemoTradingApi",Convert.ToHexString(SHA256.HashData(Values[0])).ToLowerInvariant(),Utc,ImmutableArray<OkxExecutionBalanceDetailDto>.Empty,ImmutableArray<OkxExecutionInstrumentDto>.Empty);
        }
        internal OkxReadAccount? Save(Action<string>? checkpoint=null,bool approve=true)
        {
            var type=typeof(WindowsOkxReadStore).Assembly.GetType("Apex.Desktop.Okx.OkxReadCommit");
            if(type==null)
            {
                // Exact original failing operands; helper approval is NOT OS approval.
                if(Stop.IsCancellationRequested)return null;
                checkpoint?.Invoke("CURRENT_USER_DPAPI_PREPARED");
                var a=Store.Save(Observation,Values);
                Stop.Token.ThrowIfCancellationRequested();Listed=a;return a;
            }
            Fence=Activator.CreateInstance(type,Flags,null,new object?[]{Sync,(Func<bool>)(()=>Current),Stop.Token,Observation,Values,(Action<OkxReadAccount>)(a=>Listed=a),(Func<DateTimeOffset>)(()=>Utc),(Func<long>)(()=>Ticks)},null)!;
            try
            {
                if(approve)type.GetMethod("Approve",Flags)!.Invoke(Fence,new[]{type.GetProperty("Request",Flags)!.GetValue(Fence)});
                return (OkxReadAccount?)typeof(WindowsOkxReadStore).GetMethods(Flags).Single(m=>m.Name=="Save"&&m.GetParameters().Length==4).Invoke(Store,new object?[]{Observation,Values,Fence,checkpoint});
            }
            catch(TargetInvocationException e) when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
            finally{((IDisposable)Fence).Dispose();}
        }
        internal void AssertNoEnrollment()
        {
            Assert.Null(Listed);Assert.Empty(new WindowsOkxReadStore(Root).List());
            Assert.Empty(Directory.GetFiles(Root,"*.dpapi",SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Root,"*.pending",SearchOption.AllDirectories));
            Assert.True(OriginalConfig.AsSpan().SequenceEqual(File.ReadAllBytes(Config.Path_)),"Existing config changed");
        }
        public void Dispose(){Stop.Dispose();foreach(var v in Values)CryptographicOperations.ZeroMemory(v);Directory.Delete(Root,true);}
    }
    [Theory,Trait("Gate","CommitFence")]
    [InlineData("cancel")][InlineData("generation")][InlineData("utc-expiry")][InlineData("monotonic-expiry")][InlineData("utc-rollback")][InlineData("monotonic-rollback")][InlineData("secret-binding")]
    public void Revocation_at_reached_final_fence_leaves_no_enrollment_on_restart(string mutation)
    {
        using var a=new Attempt();bool reached=false;
        try{a.Save(stage=>{Assert.Equal("CURRENT_USER_DPAPI_PREPARED",stage);reached=true;lock(a.Sync){switch(mutation){case "cancel":a.Stop.Cancel();break;case "generation":a.Current=false;break;case "utc-expiry":a.Utc=a.Utc.AddSeconds(120);break;case "monotonic-expiry":a.Ticks+=120000;break;case "utc-rollback":a.Utc=a.Utc.AddTicks(-1);break;case "monotonic-rollback":a.Ticks--;break;case "secret-binding":a.Values[1][0]^=1;break;}}});}
        catch(OperationCanceledException){}
        catch(InvalidDataException){}
        Assert.True(reached,"Final boundary not reached");a.AssertNoEnrollment();
        WindowsTestHost.Evidence("commit-fence-"+mutation,new{reachedFinalFence=true,noEnrollment=true,fullSecurePositive=false,networkCalled=false});
    }
    [Fact,Trait("Gate","CommitFence")]
    public void Cancellation_before_prepare_leaves_no_enrollment()
    {
        using var a=new Attempt();a.Stop.Cancel();try{a.Save(_=>Assert.Fail("Preparation reached after cancellation"));}catch(OperationCanceledException){}a.AssertNoEnrollment();
    }
    [Fact,Trait("Gate","CommitFence")]
    public void Completed_commit_control_remains_saved_disconnected_after_later_cancel()
    {
        using var a=new Attempt();var account=a.Save();Assert.NotNull(account);Assert.Equal(account,a.Listed);a.Stop.Cancel();
        using(var opened=new WindowsOkxReadStore(a.Root).Open(account.ScopeId)){Assert.Equal(account,opened.Account);Assert.True(a.Values.Select((v,i)=>v.AsSpan().SequenceEqual(opened.Values[i])).All(v=>v),"Roundtrip mismatch; withheld");}
        // Passive restart listing is separately exercised by the existing store test.
        Assert.Single(a.Store.List());Assert.True(a.OriginalConfig.AsSpan().SequenceEqual(File.ReadAllBytes(a.Config.Path_)),"Existing config changed");
        foreach(var secret in a.Values)foreach(var file in Directory.GetFiles(a.Root,"*",SearchOption.AllDirectories))Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(secret)>=0,"Plaintext persisted; withheld");
        WindowsTestHost.Evidence("commit-fence-positive",new{committed=true,listedAfterCancel=true,fullSecurePositive=false,networkCalled=false});
    }
}
