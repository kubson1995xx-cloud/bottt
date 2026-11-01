using System;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxNativeWaitTests
{
    [Fact]
    public async Task Sanitized_native_failure_codes_remain_distinct_and_exceptions_never_escape()
    {
        foreach(var(code,native)in new[]{("APPROVAL_PROMPT_OS_ERROR",1004u),("APPROVAL_USER_CANCELLED",1223u),("APPROVAL_AUTHENTICATION_REFUSED",1326u),("APPROVAL_UNPACK_UNSUPPORTED_METHOD",50u)})
        {
            var result=await WindowsOkxApprovalProvider.RunApprovalAsync(()=>new(false,code,native),default);
            Assert.False(result.Accepted);Assert.Equal(code+"_WIN32_"+native,result.SafeCode);
        }
        var exception=await WindowsOkxApprovalProvider.RunApprovalAsync(()=>throw new InvalidOperationException("private-synthetic-error-must-not-leak"),default);
        Assert.Equal("APPROVAL_NATIVE_EXCEPTION",exception.SafeCode);Assert.False(exception.Accepted);
    }
    [Fact]
    public async Task Timeout_is_not_cancel_and_keeps_native_ownership_until_completion()
    {
        using var release=new ManualResetEventSlim();var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result=await WindowsOkxApprovalProvider.RunApprovalAsync(()=>{try{release.Wait(TimeSpan.FromSeconds(3));return new(true,"APPROVAL_VERIFIED");}finally{finished.SetResult();}},default,TimeSpan.FromMilliseconds(25));
        try{Assert.Equal("APPROVAL_TIMEOUT_DISMISS_WINDOWS_PROMPT",result.Code);Assert.False(result.Accepted);var blocked=await WindowsOkxApprovalProvider.RunApprovalAsync(()=>throw new Exception(),default);Assert.Equal("APPROVAL_NATIVE_PENDING_DISMISS_PREVIOUS",blocked.Code);}
        finally{release.Set();await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));}
    }
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
