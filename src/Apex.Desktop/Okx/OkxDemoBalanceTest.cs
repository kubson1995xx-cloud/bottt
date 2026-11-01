using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed record OkxDemoBalanceObservation(string Uid,string Environment,string Currency,string? Cash,string? Available,DateTimeOffset ObservedUtc,string Code);
// A one-shot observation. No account service, WS, authority store, scheduler,
// baseline, activation policy or execution port exists on this call path.
internal static class OkxDemoBalanceTest
{
    private static readonly TimeSpan Budget=TimeSpan.FromSeconds(35);
    internal static async Task<OkxDemoBalanceObservation> RunAsync(OkxScopeBinding binding,Func<OkxExecutionCredentialLease> open,CancellationToken caller,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient>? factory=null)
    {
        OkxDemoBalanceObservation Fail(string code)=>new(binding.Account.AccountId,binding.Account.Environment.ToString(),binding.Account.QuoteCurrency,null,null,DateTimeOffset.UtcNow,code);
        if(binding.Account.Environment!=NavalEnvironment.OkxDemo)return Fail("DEMO_TEST_LIVE_DENIED");
        try{OkxConfiguration.ValidateBinding(binding);}catch{return Fail("DEMO_TEST_SCOPE_MISMATCH");}
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(caller);bounded.CancelAfter(Budget);var ct=bounded.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var source=open();
            if(source.Binding!=binding||source.Provenance!="DemoTradingApi")return Fail("DEMO_TEST_CREDENTIAL_ENVIRONMENT_MISMATCH");
            var scope=new OkxExecutionScope("Demo",binding.Account.AccountId,binding.MainUid,binding.Account.QuoteCurrency,binding.CredentialBindingId);
            var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
            using var request=new ReadRequest(binding,source,scope,context,ct);
            await using var reader=(factory??OkxExecutionObservationClient.Create)(scope,request,source.Fingerprint,request);
            var time=await reader.ReadTimeAsync(context,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(time.Status!=OkxObservationStatus.Observed)return Fail("DEMO_TEST_PUBLIC_TIME_"+time.ErrorCode);
            var rtt=time.ReceiveUtc-time.RequestUtc;
            if(time.Rows.Count!=1||time.Rows[0].Ts.Utc is not {} server||rtt<TimeSpan.Zero||rtt>TimeSpan.FromSeconds(2)||
                (server-(time.RequestUtc+TimeSpan.FromTicks(rtt.Ticks/2))).Duration()>TimeSpan.FromSeconds(5))return Fail("DEMO_TEST_PUBLIC_TIME_ClockPreflightFailed");
            var account=await reader.ReadAccountAsync(context,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(account.Status!=OkxObservationStatus.Observed)return Fail("DEMO_TEST_PRIVATE_AUTH_"+account.ErrorCode);
            if(account.Rows.Count!=1||account.Rows[0].Uid!=binding.Account.AccountId||account.Rows[0].MainUid!=binding.MainUid)return Fail("DEMO_TEST_ACCOUNT_BINDING_MISMATCH");
            var actual=account.Rows[0];
            // Permission is inspected only for this harmless read, never accepted as
            // evidence of trading eligibility, dedicated-key policy or activation.
            if(actual.Permissions is not ("read_only" or "read_only,trade" or "trade,read_only")||
                actual.AccountLevel!="1"||string.IsNullOrWhiteSpace(actual.Ip)||actual.AutoLoan||actual.EnableSpotBorrow||actual.SpotBorrowAutoRepay)
                return Fail("DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig");
            var balance=await reader.ReadBalancesAsync(context,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(balance.Status!=OkxObservationStatus.Observed)return Fail("DEMO_TEST_BALANCE_"+balance.ErrorCode);
            var rows=balance.Rows.SelectMany(b=>b.Details).Where(b=>b.Currency==binding.Account.QuoteCurrency).ToArray();
            if(rows.Length!=1)return Fail("DEMO_TEST_QUOTE_BALANCE_UNAVAILABLE");
            var row=rows[0];
            return new(actual.Uid,"OkxDemo",row.Currency,row.Cash.Value?.ToString(CultureInfo.InvariantCulture),row.Available.Value?.ToString(CultureInfo.InvariantCulture),balance.ReceiveUtc,"DEMO_TEST_OBSERVED_READ_ONLY_STOP");
        }
        catch(OperationCanceledException){return Fail(caller.IsCancellationRequested?"DEMO_TEST_CANCELLED":"DEMO_TEST_TIMEOUT");}
        catch(Exception e) when(e is not OutOfMemoryException){return Fail("DEMO_TEST_CREDENTIAL_OR_LOCAL_READ_REFUSED");}
    }
    // Private, request-local lifetime fence, not Connect/Observe authority. Neither
    // scope nor context comes from an HTTP approval payload; only three typed GETs
    // above are reachable. The transport separately checks material fingerprint.
    private sealed class ReadRequest(OkxScopeBinding binding,OkxExecutionCredentialLease source,OkxExecutionScope scope,
        OkxObservationContext context,CancellationToken cancellation):IOkxSigningMaterialProvider,IOkxObservationReadFence,IDisposable
    {
        private readonly string fingerprint=source.Fingerprint;
        private readonly DateTimeOffset startUtc=DateTimeOffset.UtcNow;
        private readonly long startMonotonic=TimeProvider.System.GetTimestamp();
        private int closed;
        public bool CanObserve(OkxExecutionScope requestedScope,OkxObservationContext requestedContext,DateTimeOffset utcNow)=>
            Volatile.Read(ref closed)==0&&!cancellation.IsCancellationRequested&&requestedScope==scope&&requestedContext==context&&
            scope.Environment=="Demo"&&source.Binding==binding&&source.Provenance=="DemoTradingApi"&&source.Fingerprint==fingerprint&&
            utcNow>=startUtc&&utcNow-startUtc<Budget&&TimeProvider.System.GetElapsedTime(startMonotonic) is var elapsed&&elapsed>=TimeSpan.Zero&&elapsed<Budget;
        public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(!CanObserve(scope,context,DateTimeOffset.UtcNow))throw new InvalidOperationException("DEMO_READ_REQUEST_EXPIRED");
            return ValueTask.FromResult(source.OpenSigningMaterial(scope));
        }
        public void Dispose()=>Interlocked.Exchange(ref closed,1);
    }
}
