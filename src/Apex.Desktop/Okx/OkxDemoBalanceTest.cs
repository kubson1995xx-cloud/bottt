using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed record OkxDemoBalanceObservation(string Uid,string Environment,string Currency,string? Cash,string? Available,DateTimeOffset ObservedUtc,string Code);
// A one-shot observation. No account service, WS, authority store, scheduler,
// baseline, activation policy or execution port exists on this call path.
internal static class OkxDemoBalanceTest
{
    internal static async Task<OkxDemoBalanceObservation> RunAsync(OkxScopeBinding binding,Func<OkxExecutionCredentialLease> open,CancellationToken caller,
        Func<IOkxDemoSecretProvider,OkxReadScope,OkxEeaDemoReadClient>? factory=null)
    {
        OkxDemoBalanceObservation Fail(string code)=>new(binding.Account.AccountId,binding.Account.Environment.ToString(),binding.Account.QuoteCurrency,null,null,DateTimeOffset.UtcNow,code);
        if(binding.Account.Environment!=NavalEnvironment.OkxDemo)return Fail("DEMO_TEST_LIVE_DENIED");
        try{OkxConfiguration.ValidateBinding(binding);}catch{return Fail("DEMO_TEST_SCOPE_MISMATCH");}
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(caller);bounded.CancelAfter(TimeSpan.FromSeconds(35));var ct=bounded.Token;
        try
        {
            using var source=open();
            if(source.Binding!=binding||source.Provenance!="DemoTradingApi")return Fail("DEMO_TEST_CREDENTIAL_ENVIRONMENT_MISMATCH");
            var provider=new ReadSecrets(binding,source);
            var scope=new OkxReadScope(binding.CredentialBindingId,binding.Account.AccountId);
            await using var reader=(factory??OkxEeaDemoReadClient.CreateDemoRead)(provider,scope);
            var time=await reader.GetTimeAsync(ct).ConfigureAwait(false);
            if(time.Status!=OkxReadStatus.Observed)return Fail("DEMO_TEST_PUBLIC_TIME_"+time.ErrorCode);
            var account=await reader.GetConfigAsync(ct).ConfigureAwait(false);
            if(account.Status!=OkxReadStatus.Observed)return Fail("DEMO_TEST_PRIVATE_AUTH_"+account.ErrorCode);
            if(account.Rows.Length!=1||account.Rows[0].Uid!=binding.Account.AccountId||account.Rows[0].MainUid!=binding.MainUid)return Fail("DEMO_TEST_ACCOUNT_BINDING_MISMATCH");
            var balance=await reader.GetBalancesAsync(ct).ConfigureAwait(false);
            if(balance.Status!=OkxReadStatus.Observed)return Fail("DEMO_TEST_BALANCE_"+balance.ErrorCode);
            var rows=balance.Rows.SelectMany(b=>b.Details).Where(b=>b.Ccy==binding.Account.QuoteCurrency).ToArray();
            if(rows.Length!=1)return Fail("DEMO_TEST_QUOTE_BALANCE_UNAVAILABLE");
            var row=rows[0];
            return new(account.Rows[0].Uid,"OkxDemo",row.Ccy,row.CashBal.Value?.ToString(CultureInfo.InvariantCulture),row.AvailBal.Value?.ToString(CultureInfo.InvariantCulture),balance.ReceiveUtc,"DEMO_TEST_OBSERVED_READ_ONLY_STOP");
        }
        catch(OperationCanceledException){return Fail(caller.IsCancellationRequested?"DEMO_TEST_CANCELLED":"DEMO_TEST_TIMEOUT");}
        catch(Exception e) when(e is not OutOfMemoryException){return Fail("DEMO_TEST_CREDENTIAL_OR_LOCAL_READ_REFUSED");}
    }
    private sealed class ReadSecrets(OkxScopeBinding binding,OkxExecutionCredentialLease source):IOkxDemoSecretProvider
    {
        public ValueTask<OkxDemoCredentialLease> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(binding.Account.Environment!=NavalEnvironment.OkxDemo||source.Binding!=binding||source.Provenance!="DemoTradingApi")throw new InvalidOperationException("DEMO_CREDENTIAL_REQUIRED");
            return ValueTask.FromResult(source.OpenDemoReadMaterial());
        }
    }
}
