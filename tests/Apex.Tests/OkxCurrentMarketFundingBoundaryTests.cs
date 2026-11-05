using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxCurrentMarketFundingBoundaryTests
{
    internal static NavalFiniteOperatorPremise Premise(OperationalPeer f)=>(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);
    private static void Setup(OperationalPeer f,FiniteDemoPeer peer)
    {f.Mutate=(p,b)=>OkxCurrentMarketFundingTests.Shape(f,peer,p,b);f.Runtime.FiniteFundingPremise=Premise(f);f.BaselineOverride=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);}
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    [Theory]
    [InlineData("none")][InlineData("expired")][InlineData("future")][InlineData("statement")][InlineData("policy")][InlineData("test")][InlineData("environment")][InlineData("provenance")]
    public async Task Operator_premise_is_separate_and_never_authorizes_nullable_money_alone(string mutation)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA"};Setup(f,peer);var p=f.Runtime.FiniteFundingPremise!;
        f.Runtime.FiniteFundingPremise=mutation switch{"none"=>null,"expired"=>p with{ExpiresUtc=f.Clock.GetUtcNow().AddSeconds(-1)},"future"=>p with{IssuedUtc=f.Clock.GetUtcNow().AddMinutes(1)},"statement"=>p with{Statement="unknown"},"policy"=>p with{Policy="other"},"test"=>p with{TestId="other"},"environment"=>p with{Environment="Live"},_=>p with{Provenance="exchange-verified"}};
        Assert.False(await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default));Assert.False(File.Exists(f.Target));Assert.Empty(peer.Sends);
        Assert.DoesNotContain(f.Paths,p=>p.Contains("interest-limits",StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("debt")][InlineData("interest")][InlineData("aggregate-missing")][InlineData("usedLmt")][InlineData("record-interest")][InlineData("interestFreeLiab")][InlineData("potentialBorrowingAmt")]
    [InlineData("returned-unavailable")][InlineData("empty-records")][InlineData("duplicate")][InlineData("underflow")][InlineData("excluded-debt")][InlineData("negative")]
    public async Task Exact_current_unfiltered_money_and_all_raw_rows_veto_before_owner(string mutation)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA"};Setup(f,peer);
        f.Mutate=(path,body)=>
        {
            OkxCurrentMarketFundingTests.Shape(f,peer,path,body);
            if(path=="/api/v5/account/balance"&&mutation=="excluded-debt")body["data"]![0]!["details"]![2]!["crossLiab"]="0.00000001";
            if(path!="/api/v5/account/interest-limits")return;Assert.EndsWith("?type=2",f.Paths.Last());var r=body["data"]![0]!;var records=r["records"]!.AsArray();
            if(mutation is "debt" or "interest")r[mutation]="1";
            if(mutation=="aggregate-missing")r["debt"]="";
            if(mutation is "usedLmt" or "interestFreeLiab" or "potentialBorrowingAmt")records[5]![mutation]="0.00000001";
            if(mutation=="record-interest")records[5]!["interest"]="1";
            if(mutation=="returned-unavailable")records[5]!["potentialBorrowingAmt"]="";
            if(mutation=="empty-records")records.Clear();if(mutation=="duplicate")records.Add(records[0]!.DeepClone());
            if(mutation=="underflow")records[5]!["interest"]="0.00000000000000000000000000001";
            if(mutation=="negative")records[5]!["usedLmt"]="-1";
        };
        Assert.False(await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default));Assert.False(File.Exists(f.Target));Assert.Empty(peer.Sends);
    }
    [Fact]
    public async Task Core_neutral_structural_bindings_seals_and_freshness_do_not_admit_replay_or_all_product_clearance()=>await OkxSyntheticClockIsolationTests.Run("current-funding-core");
    internal static async Task CoreScenario()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA"};Setup(f,peer);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var b=State(f).NavalBroker!.Baseline;var o=b.Observations;var e=o.CurrentMarketFunding!;var p=o.OperatorPremise!;
        Assert.Equal(6,e.RecordCount);Assert.Equal(3,o.Balances.Count(b=>!e.Records.Any(r=>r.Currency==b.Currency)));Assert.Equal("NOT_ESTABLISHED",e.AllProductClearance);
        Assert.Equal(1234.56m,NavalBrokerValidation.ValidateBatch(b.Binding,o));
        var mutants=new[]{e with{Connection=Guid.NewGuid()},e with{Capture=Guid.NewGuid()},e with{Uid="other"},e with{MainUid="other"},e with{CredentialBindingId="other"},e with{CredentialFingerprint=new string('b',64)},e with{Environment="Live"},e with{AllProductClearance="ESTABLISHED"},e with{Response=e.Response with{Query="?type=2&ccy=USDC"}},e with{Response=e.Response with{Digest="bad"}},e with{RecordCount=0},e with{Records=e.Records.Take(5).ToArray()},e with{DebtUsd=new("0.00000000000000000000000000001",0)}};
        foreach(var m in mutants)Assert.Throws<InvalidDataException>(()=>NavalBrokerValidation.ValidateBatch(b.Binding,o with{CurrentMarketFunding=m}));
        Assert.Throws<InvalidDataException>(()=>NavalBrokerValidation.ValidateBatch(b.Binding,o with{OperatorPremise=null}));
        Assert.Throws<InvalidDataException>(()=>NavalBrokerValidation.ValidateBatch(b.Binding,o with{CurrentMarketFunding=null,OperatorPremise=null}));
        Assert.False(NavalCurrentMarketFunding.Fresh(e,p,e.Response.ReceiveUtc.AddSeconds(31),e.Response.ReceiveTimestamp,e.TimestampFrequency,o.CaptureUtc,o.CaptureTimestamp));
        Assert.False(NavalCurrentMarketFunding.Fresh(e,p,e.Response.ReceiveUtc,e.Response.RequestTimestamp-1,e.TimestampFrequency,o.CaptureUtc,o.CaptureTimestamp));
        Assert.False(NavalCurrentMarketFunding.Fresh(e,p,e.Response.RequestUtc.AddSeconds(-1),e.Response.ReceiveTimestamp,e.TimestampFrequency,o.CaptureUtc,o.CaptureTimestamp));
        // Genuine pre-change fixture is exercised separately; this new nullable seal
        // reopens as history even when no fresh monetary read is available.
        await f.Service.DisconnectAsync();int reads=f.Paths.Count;Assert.True(f.Service.OpenExisting());Assert.Equal(reads,f.Paths.Count);Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
    }
    [Theory]
    [InlineData("after-pending")][InlineData("after-protection-cancel")][InlineData("excluded-movement")][InlineData("unknown-submit")][InlineData("unknown-cancel")]
    public async Task Fresh_witness_loss_or_unknown_never_releases_or_rebuys_and_exit_reports_residual(string mutation)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",OwnFreezes=true,UnknownSubmit=mutation=="unknown-submit",UnknownCancel=mutation=="unknown-cancel"};Setup(f,peer);bool broken=false;
        f.Mutate=(path,body)=>{OkxCurrentMarketFundingTests.Shape(f,peer,path,body);if(mutation=="after-protection-cancel"&&peer.ProtectionCanceled)broken=true;if(broken&&path=="/api/v5/account/interest-limits")body["data"]![0]!["debt"]="1";if(mutation=="excluded-movement"&&peer.Canceled&&path=="/api/v5/account/balance")body["data"]![0]!["details"]![2]!["cashBal"]="2";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        string result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),stage=>{if(mutation=="after-pending"&&stage=="PENDING_CANCEL_RESULT")broken=true;},default);
        Assert.DoesNotContain("COMPLETED_STOP",result);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Armed);
        if(mutation=="after-protection-cancel"){Assert.True(peer.ProtectionCanceled);Assert.True(State(f).Naval!.Reservations.Sum(r=>r.Inventory)>0);Assert.Equal("",peer.SellClient);Assert.Contains("CURRENT_MARKET_FUNDING_UNPROVEN",State(f).NavalBroker!.Operational!.Obligations);}
        else Assert.Equal("",peer.BuyClient);
    }
    [Fact]
    public async Task Production_selection_same_nullable_shape_nominates_only_unheld_base_without_summing_excluded()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA"};Setup(f,peer);
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        var scope=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC",f.Binding.CredentialBindingId);
        var n=await OkxFiniteAccountSelection.ObserveFundedAsync(scope,credentials.ForObservation(scope),credentials.Fingerprint,()=>true,default,Premise(f),null,f.Clock,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;}),peer);
        Assert.Equal("USDC",n.Quote);Assert.Equal("ADA-USDC",n.Instrument.Instrument);Assert.Equal(1234.56m,n.Cash);Assert.True(n.Plan.Debit<=10);Assert.Contains("/api/v5/account/interest-limits?type=2",f.Paths);Assert.Empty(peer.Sends);
    }
}
