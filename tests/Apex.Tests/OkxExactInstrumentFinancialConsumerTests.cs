using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed class OkxExactInstrumentFinancialConsumerTests
{
    // Removing empty support prevents legitimate signed finite admission. Removing
    // its value/type/SPOT guard permits unsupported data before durable ownership.
    [Theory]
    [InlineData("empty",true)]
    [InlineData("freq",false)]
    [InlineData("method",false)]
    [InlineData("type",false)]
    [InlineData("nonspot",false)]
    public async Task Empty_only_SPOT_extensions_preserve_signed_baseline_and_refuse_before_owner(string shape,bool accepted)
    {
        await using var f=new OperationalPeer(finite:true);
        f.Mutate=(p,b)=>
        {
            OkxFiniteMultiCurrencyTests.OriginalSix(p,b);
            if(p=="/api/v5/account/balance")foreach(var r in b["data"]![0]!["details"]!.AsArray())
            {r!["autoLendAmt"]="0";r["autoStakingStatus"]="off";}
            if(p!="/api/v5/account/instruments")return;
            foreach(var r in b["data"]!.AsArray())
            {
                r!["freq"]=shape=="freq"?"SYNTHETIC_NONEMPTY_FREQ":"";
                r["method"]=shape=="method"?"SYNTHETIC_NONEMPTY_METHOD":"";
                if(shape=="type")r["method"]=JsonValue.Create(false);
                if(shape=="nonspot")r["instType"]="MARGIN";
            }
        };
        f.BaselineOverride=new SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        Assert.Equal(accepted,await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default));
        Assert.All(f.Methods,m=>Assert.Equal(System.Net.Http.HttpMethod.Get,m));
        if(!accepted)
        {
            Assert.False(File.Exists(f.Target));Assert.Null(f.Service.Coordinator);
            Assert.Equal("/api/v5/account/instruments?instType=SPOT",f.Paths.Last());
            int sends=f.Paths.Count;Assert.False(await f.Service.ApproveAndCreateAsync(default));Assert.Equal(sends,f.Paths.Count);
            Assert.False(File.Exists(f.Target));Assert.Null(f.Service.Coordinator);return;
        }
        Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var state=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
        Assert.Equal(6,state.NavalBroker!.Baseline.Observations.Balances.Count);
        Assert.Equal(1234.56m,state.NavalBroker.Baseline.InitialCash);Assert.Equal(1234.56m,state.Naval!.Accounts.Single().Cash);
        Assert.Equal("ADA-USDC",Assert.Single(state.NavalBroker.Baseline.Observations.Catalog).Instrument);
        Assert.Empty(state.Naval.Reservations);Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
    }
}
