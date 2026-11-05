using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxCurrentMarketFundingTests
{
    // Pinned synthetic shape: six raw currencies, interest unavailable on all;
    // four nullable liabilities, two explicit zero. Three balance currencies have
    // no Market record; unrelated Market records are retained, never summed.
    internal static void Shape(OperationalPeer f,FiniteDemoPeer peer,string path,JsonNode body)
    {
        peer.Observe(path,body);OkxFiniteMultiCurrencyTests.OriginalSix(path,body);
        if(path=="/api/v5/account/balance")
        {
            var rows=body["data"]![0]!["details"]!.AsArray();
            foreach(var r in rows){r!["interest"]="";if(r["ccy"]!.GetValue<string>() is not ("BTC" or "ETH"))foreach(var k in new[]{"liab","crossLiab","isoLiab"})r[k]="";}
            body["data"]![0]!["uTime"]=f.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString();
        }
        if(path=="/api/v5/account/interest-limits")
        {
            body["data"]=new JsonArray(new JsonObject{["debt"]="0",["interest"]="0",["loanAlloc"]="",["nextDiscountTime"]="",["nextInterestTime"]="",["records"]=new JsonArray(new[]{"USDC","BTC","ETH","LTC","BCH","ETC"}.Select(c=>(JsonNode)new JsonObject{["ccy"]=c,["usedLmt"]="0",["interest"]="0",["interestFreeLiab"]="0",["potentialBorrowingAmt"]="0"}).ToArray())});
        }
    }
    internal static object Premise(OperationalPeer f)
    {
        var type=typeof(NavalBrokerBalance).Assembly.GetType("Apex.Core.Naval.NavalFiniteOperatorPremise");
        // Old-DLL causal replay intentionally has no premise extension. The same
        // operator-confirmed operands remain pinned in this original tester DLL.
        if(type==null)return new object();
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(new{Schema="finite-operator-premise-v1",Policy="current-market-funding-v1",TestId="current-market-funding-bounded-demo-v1",CredentialRecordDigest=new string('a',64),Statement="Nie będę wykonywać innych operacji podczas\nWirtualne środki i nie korzystałem z pożyczek",Provenance="parent-confirmed-user",IssuedUtc=f.Clock.GetUtcNow().AddSeconds(-1),ExpiresUtc=f.Clock.GetUtcNow().AddMinutes(10),Environment="OkxDemo"}),type)!;
    }
    [Fact]
    public async Task Original_nullable_six_Market_zero_operator_confirmed_reaches_owner_fee_protection_exit_reconcile_reopen_STOP()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",OwnFreezes=true};
        f.Mutate=(p,b)=>Shape(f,peer,p,b);
        typeof(OkxNavalRuntime).GetProperty("FiniteFundingPremise",BindingFlags.NonPublic|BindingFlags.Instance)?.SetValue(f.Runtime,Premise(f));
        f.BaselineOverride=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        Assert.True(await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default),"TARGET_FINANCIAL_GATE:"+f.Service.Status);
        Assert.True(await f.Service.ApproveAndCreateAsync(default),f.Service.Status);
        EngineState State()=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
        var before=State();Assert.Equal(6,before.NavalBroker!.Baseline.Observations.Balances.Count);
        Assert.All(before.NavalBroker.Baseline.Observations.Balances,b=>Assert.Null(b.Interest));
        Assert.Equal(4,before.NavalBroker.Baseline.Observations.Balances.Count(b=>b.Liability==null));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default));
        Assert.Equal(5,peer.Sends.Count);Assert.Equal(2,State().Naval!.Fills.Count);Assert.Equal(.0000998m,State().Naval!.Reservations.Sum(r=>r.Inventory));
        Assert.All(State().NavalBroker!.Operational!.LastBalances!,b=>Assert.Null(b.Interest));
        await loop.ReconcileAsync(default);Assert.Equal(2,State().Naval!.Fills.Count);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting(),f.Service.Status);Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Equal(5,peer.Sends.Count);
    }
}
