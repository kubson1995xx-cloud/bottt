using System;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxNativeWaitTests
{
    [Fact]
    public async Task Cancellation_returns_without_waiting_for_native_UI_and_does_not_allow_a_second_pending_prompt()
    {
        using var release=new ManualResetEventSlim();using var ct=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting=WindowsOkxApprovalProvider.RunConfirmationAsync(()=>{entered.SetResult();try{release.Wait(TimeSpan.FromSeconds(5));return true;}finally{finished.SetResult();}},ct.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));ct.Cancel();
            Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(await WindowsOkxApprovalProvider.RunConfirmationAsync(()=>throw new Exception("Second prompt must not execute"),default).WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally{release.Set();await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        // Completion of the actual operation releases prompt ownership, never a cached permission.
        bool positive=false;var deadline=DateTime.UtcNow.AddSeconds(2);
        while(!positive&&DateTime.UtcNow<deadline){positive=await WindowsOkxApprovalProvider.RunConfirmationAsync(()=>true,default);if(!positive)await Task.Delay(10);}
        Assert.True(positive);
    }
}
