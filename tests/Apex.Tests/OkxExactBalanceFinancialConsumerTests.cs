using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed class OkxExactBalanceFinancialConsumerTests
{
    // Removing the parser extension breaks this legitimate six-row baseline;
    // dropping its economic restrictions admits the unsafe rows before owner creation.
    [Theory]
    [InlineData("inactive",true)]
    [InlineData("amount",false)]
    [InlineData("staking",false)]
    public async Task Exact_extensions_reach_unchanged_signed_baseline_and_clean_balance_guards(string shape,bool accepted)
    {
        await using var f=new OperationalPeer(finite:true);
        f.Mutate=(p,b)=>
        {
            OkxFiniteMultiCurrencyTests.OriginalSix(p,b);
            if(p!="/api/v5/account/balance")return;
            foreach(var row in b["data"]![0]!["details"]!.AsArray())
            {row!["autoLendAmt"]=shape=="amount"?"2.75":"0";row["autoStakingStatus"]=shape=="staking"?"active":"off";}
        };
        f.BaselineOverride=new SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        Assert.Equal(accepted,await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default));
        if(!accepted){Assert.False(File.Exists(f.Target));Assert.Null(f.Service.Coordinator);return;}
        Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var state=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
        Assert.Equal(6,state.NavalBroker!.Baseline.Observations.Balances.Count);
        Assert.Equal(1234.56m,state.NavalBroker.Baseline.InitialCash);
        Assert.Equal(1234.56m,state.Naval!.Accounts.Single().Cash);Assert.Empty(state.Naval.Reservations);
        Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
    }
}
