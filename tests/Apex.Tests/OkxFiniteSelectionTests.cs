using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxFiniteSelectionTests
{
    [Fact]
    public async Task Individually_insufficient_quotes_are_not_pooled_and_refusal_retains_pre_send_operands()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA"};
        f.Mutate=(p,b)=>{peer.Observe(p,b);OkxFiniteMultiCurrencyTests.OriginalSix(p,b);
            if(p=="/api/v5/account/balance"){var q=b["data"]![0]!["details"]![0]!;foreach(var k in new[]{"cashBal","availBal","eq"})q[k]="0.02";var euro=q.DeepClone();euro["ccy"]="EUR";b["data"]![0]!["details"]!.AsArray().Add(euro);}
            if(p=="/api/v5/account/instruments"){var a=b["data"]!.AsArray().Single(v=>v!["baseCcy"]!.GetValue<string>()=="ADA")!.DeepClone();a["instId"]="ADA-EUR";a["quoteCcy"]="EUR";a["tradeQuoteCcyList"]=new JsonArray("EUR");b["data"]!.AsArray().Add(a);}};
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        var scope=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC",f.Binding.CredentialBindingId);
        var refusal=await Assert.ThrowsAsync<OkxFiniteAccountRefusal>(()=>OkxFiniteAccountSelection.ObserveAsync(scope,credentials.ForObservation(scope),credentials.Fingerprint,()=>true,default,null,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(delay,ct)=>{f.Clock.Advance(delay);return Task.CompletedTask;}),peer));
        Assert.Equal("FINITE_NO_FEASIBLE_ROUNDTRIP_FOR_FUNDED_QUOTES",refusal.Code);string operands=System.Text.Json.JsonSerializer.Serialize(refusal.Operands);
        Assert.Contains("FINITE_SELECTED_QUOTE_CASH_OR_RISK_INSUFFICIENT",operands);Assert.Contains("0.022",operands);Assert.Empty(peer.Sends);Assert.False(System.IO.File.Exists(f.Target));
    }
    [Theory]
    [InlineData("1234.56","EUR")]
    [InlineData("0.01","USDC")]
    public async Task Funded_quote_priority_uses_own_fee_group_roundtrip_and_cash_without_currency_sum(string euro,string selected)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA"};
        f.Mutate=(p,b)=>{
            peer.Observe(p,b);OkxFiniteMultiCurrencyTests.OriginalSix(p,b);
            if(p=="/api/v5/account/balance")
            {var e=b["data"]![0]!["details"]![0]!.DeepClone();e["ccy"]="EUR";foreach(var k in new[]{"cashBal","availBal","eq"})e[k]=euro;b["data"]![0]!["details"]!.AsArray().Add(e);}
            if(p=="/api/v5/account/instruments")
            {var a=b["data"]!.AsArray().Single(v=>v!["baseCcy"]!.GetValue<string>()=="ADA")!;a["groupId"]="9";var e=a.DeepClone();e["instId"]="ADA-EUR";e["quoteCcy"]="EUR";e["tradeQuoteCcyList"]=new JsonArray("EUR");b["data"]!.AsArray().Add(e);}
            if(p=="/api/v5/account/trade-fee"&&f.Paths.Last().Contains("ADA-",StringComparison.Ordinal))
            {var g=b["data"]![0]!["feeGroup"]![0]!;g["groupId"]="9";g["maker"]="-0.001";g["taker"]="-0.002";}
        };
        await f.Connect();object? diagnostics=null;
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        var nomination=await OkxFiniteAccountSelection.ObserveAsync(f.Service.ExecutionScope!,credentials.ForObservation(f.Service.ExecutionScope!),credentials.Fingerprint,()=>true,default,
            d=>diagnostics=d,f.Clock,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(delay,ct)=>{f.Clock.Advance(delay);return Task.CompletedTask;}),peer);
        Assert.NotNull(diagnostics);Assert.All(f.Methods,m=>Assert.Equal(System.Net.Http.HttpMethod.Get,m));
        Assert.Equal(selected,nomination.Quote);Assert.Equal("ADA-"+selected,nomination.Instrument.Instrument);
        Assert.Equal(.002m,nomination.Fees.EntryRate);Assert.Equal(1234.56m,nomination.Cash);Assert.True(nomination.Plan.Debit<=10);
        Assert.DoesNotContain(f.Paths,p=>p.Contains("trade-fee?instId=BTC-",StringComparison.Ordinal));
    }
}
