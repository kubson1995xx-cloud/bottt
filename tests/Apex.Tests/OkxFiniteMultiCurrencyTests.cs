using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Core.Engine;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxFiniteMultiCurrencyTests
{
    // Verbatim reviewer balance operands: quote 1234.56 plus BTC/ETH/SOL/XRP/DOGE = 1 each.
    // ADA is an additional supported, unheld catalog candidate, never an invented balance.
    internal static void OriginalSix(string path,JsonNode body,bool unheldCatalog=true)
    {
        if(path=="/api/v5/account/balance")
        {
            var details=body["data"]![0]!["details"]!.AsArray();var q=details[0]!;
            foreach(string c in new[]{"BTC","ETH","SOL","XRP","DOGE"})
            {var copy=q.DeepClone();copy["ccy"]=c;foreach(string k in new[]{"cashBal","availBal","eq"})copy[k]="1";copy["frozenBal"]="0";copy["ordFrozen"]="0";details.Add(copy);}
        }
        if(path=="/api/v5/account/instruments"&&unheldCatalog)
        {var a=body["data"]![0]!.DeepClone();a["instId"]="ADA-USDC";a["baseCcy"]="ADA";body["data"]!.AsArray().Add(a);}
    }
    [Fact]
    public async Task Original_six_balance_operands_connect_approve_and_create_same_durable_owner_with_unheld_supported_base()
    {
        await using var f=new OperationalPeer(finite:true);f.Mutate=(p,b)=>OriginalSix(p,b);
        bool connected=await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default);
        Assert.True(connected,f.Service.Status);Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var state=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
        Assert.Equal(6,state.NavalBroker!.Baseline.Observations.Balances.Count);
        Assert.Equal("ADA-USDC",Assert.Single(state.NavalBroker.Baseline.Observations.Catalog).Instrument);
        Assert.Equal(1234.56m,state.Naval!.Accounts.Single().Cash);Assert.Empty(state.Naval.Reservations);
        Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
    }
    [Fact]
    public async Task Original_six_with_only_held_BTC_catalog_refuses_specific_no_unheld_base_before_owner()
    {
        await using var f=new OperationalPeer(finite:true);f.Mutate=(p,b)=>OriginalSix(p,b,false);
        Assert.False(await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default));
        Assert.Equal("FINITE_NO_UNHELD_SUPPORTED_BASE",f.Service.Status);Assert.False(File.Exists(f.Target));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Six_rows_own_ADA_fee_group_full_sequence_exact_exclusions_dust_replay_and_reopen_STOP(bool grossProtection)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",FeeRate=.002m,GrossProtection=grossProtection};
        f.Mutate=(p,b)=>MultiPeer(f,peer,p,b);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var initial=State(f).NavalBroker!.Baseline;Assert.Equal(.002m,initial.Observations.Fees.EntryRate);
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        bool gap=false;
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{var current=State(f);if(current.Naval!.Fills.Count==1&&!peer.ProtectionCanceled)gap|=current.NavalBroker?.Operational?.Obligations.Any(o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal))==true;},default);
        Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",result);Assert.Equal(grossProtection,gap);
        Assert.Equal(5,peer.Sends.Count);Assert.Equal(.0001m,peer.SellQuantity);Assert.Equal(.0002m,peer.BuyQuantity);
        var state=State(f);Assert.Equal(2,state.Naval!.Fills.Count);Assert.All(state.Naval.Fills,p=>Assert.Equal("ADA-USDC",p.Fill.Instrument));
        Assert.Equal(.0000996m,state.Naval.Reservations.Sum(r=>r.Inventory));
        foreach(var excluded in initial.Observations.Balances.Where(v=>v.Currency!="USDC"))
        {var actual=state.NavalBroker!.Operational!.LastBalances!.Single(v=>v.Currency==excluded.Currency);Assert.Equal(excluded,actual);}
        Assert.Equal(1234.56m,initial.InitialCash);Assert.True(state.Naval.LiquidationEquity<1234.56m);
        Assert.All(state.Naval.Reservations,r=>Assert.Equal("ADA-USDC",r.Intent.Instrument));
        int fills=state.Naval.Fills.Count;await loop.ReconcileAsync(default);Assert.Equal(fills,State(f).Naval!.Fills.Count);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
        Assert.Equal(initial.Digest,State(f).NavalBroker!.Baseline.Digest);Assert.Equal(5,peer.Sends.Count);
    }
    [Fact]
    public async Task Excluded_movement_after_terminal_pending_cancel_stops_before_additional_risk()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",FeeRate=.002m};bool changed=false;
        f.Mutate=(p,b)=>{MultiPeer(f,peer,p,b);if(changed&&p=="/api/v5/account/balance")foreach(var row in b["data"]![0]!["details"]!.AsArray().Where(v=>v!["ccy"]!.GetValue<string>()=="BTC"))foreach(var k in new[]{"cashBal","availBal","eq"})row![k]="2";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),stage=>{if(stage=="PENDING_CANCEL_RESULT")changed=true;},default);
        Assert.Equal("PENDING_NOT_PROVEN_ZERO_FILL_CANCELED_NO_SECOND_BUY",result);Assert.Equal(2,peer.Sends.Count);Assert.Equal("",peer.BuyClient);
        Assert.Contains("BROKER_BALANCE_DISCREPANCY",State(f).NavalBroker!.Operational!.Obligations);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Armed);
    }
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    [Fact]
    public async Task Proven_own_pending_quote_and_attached_base_freezes_do_not_import_foreign_assets_or_block_exact_cleanup()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",FeeRate=.002m,OwnFreezes=true};
        f.Mutate=(p,b)=>{MultiPeer(f,peer,p,b);if(p=="/api/v5/account/balance"){var rows=b["data"]![0]!["details"]!.AsArray();if(!rows.Any(v=>v!["ccy"]!.GetValue<string>()=="ADA")){var zero=rows.Single(v=>v!["ccy"]!.GetValue<string>()=="BTC")!.DeepClone();zero["ccy"]="ADA";foreach(var k in new[]{"cashBal","availBal","eq"})zero[k]="0";rows.Add(zero);}}};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",result);Assert.Equal(5,peer.Sends.Count);Assert.Equal(.0000996m,State(f).Naval!.Reservations.Sum(r=>r.Inventory));
    }
    [Fact]
    public async Task Runner_signed_baseline_accepts_full_six_row_snapshot_and_only_nominated_cash()
    {
        await using var f=new OperationalPeer(finite:true);f.Mutate=(p,b)=>OriginalSix(p,b);
        f.BaselineOverride=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        Assert.True(await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default),f.Service.Status);
        Assert.True(await f.Service.ApproveAndCreateAsync(default));Assert.Equal(1234.56m,State(f).NavalBroker!.Baseline.InitialCash);
    }
    [Fact]
    public void Genuine_pre_R1_production_DLL_baseline_retains_canonical_seals_without_guessed_extension()
    {
        using var stream=typeof(OkxFiniteMultiCurrencyTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.okx-pre-r1-broker-baseline.json")!;
        using var reader=new StreamReader(stream);string raw=reader.ReadToEnd();using var doc=JsonDocument.Parse(raw);
        NavalBrokerRaw.Require(doc.RootElement);var state=JsonSerializer.Deserialize<EngineState>(raw,NavalJson.Options)!;
        Assert.Null(state.NavalBroker!.Baseline.Observations.FiniteAllocation);Assert.DoesNotContain("FiniteAllocation",NavalJson.Serialize(state));
        NavalBrokerValidation.Validate(state,NavalBrokerValidation.Definition(state.NavalBroker.Baseline));
    }
    [Fact]
    public async Task Runner_nomination_signed_baseline_to_same_owner_full_sequence_and_private_scope_report()
        =>await OkxSyntheticClockIsolationTests.Run("finite-r1-nomination");
    internal static async Task NominationScenario()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",FeeRate=.002m};f.Mutate=(p,b)=>MultiPeer(f,peer,p,b);
        await f.Connect();using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        var nomination=await OkxFiniteAccountSelection.ObserveAsync(f.Service.ExecutionScope!,credentials.ForObservation(f.Service.ExecutionScope!),credentials.Fingerprint,()=>true,default,null,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(delay,ct)=>{f.Clock.Advance(delay);return Task.CompletedTask;}),peer);
        await f.Service.DisconnectAsync();
        await using var runtime=OkxNavalRuntime.ComposeFiniteDemo(f.Root,f.Binding,f,()=>true,nomination);
        OkxAccountConnectionService? service=null;
        var approval=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>service);
        await using var account=service=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,approval,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(delay,ct)=>{f.Clock.Advance(delay);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,ct)=>System.Threading.Tasks.Task.FromResult<System.Net.WebSockets.WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x));
        Assert.True(await account.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default),account.Status);Assert.True(await account.ApproveAndCreateAsync(default,refresh:true),account.Status);
        await using var loop=OkxOperationalTradingService.Create(account,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,account,Guid.NewGuid().ToString("N"),_=>{},default);Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",result);
        var state=State(f);var report=Apex.Okx.DemoAcceptance.TransactionReport.Build(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,true,result,Array.Empty<string>(),state,null,loop.FiniteDiagnostics(),false,nomination);
        Assert.False(credentials.ContainsSecret(report));using var doc=JsonDocument.Parse(report);var scope=doc.RootElement.GetProperty("financialScope");
        Assert.False(scope.GetProperty("excludedSpendOrSellAuthority").GetBoolean());Assert.Equal(5,scope.GetProperty("excludedObservedHoldings").GetArrayLength());
        Assert.Equal(1234.56m,scope.GetProperty("includedQuoteCash").GetDecimal());Assert.Equal(7,doc.RootElement.GetProperty("balanceDeltas").GetArrayLength());
        Assert.False(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Equal(5,peer.Sends.Count);
    }
    [Fact]
    public async Task Full_snapshot_semantics_and_typed_session_required_even_with_recomputed_seals()
    {
        await using var f=new OperationalPeer(finite:true);f.Mutate=(p,b)=>OriginalSix(p,b);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var baseline=State(f).NavalBroker!.Baseline;var o=baseline.Observations;var a=o.FiniteAllocation!;
        Assert.Equal(1234.56m,NavalBrokerValidation.ValidateBatch(baseline.Binding,o));
        var variants=new[]{o with{Balances=o.Balances.Append(o.Balances[0]).ToArray()},o with{FiniteAllocation=a with{Connection=Guid.NewGuid()}},
            o with{FiniteAllocation=a with{Excluded=Array.Empty<NavalBrokerBalance>()}},o with{FiniteAllocation=a with{Quote="EUR"}},
            o with{Balances=o.Balances.Select(v=>v.Currency=="BTC"?v with{Liability=1}:v).ToArray()},
            o with{Balances=o.Balances.Select(v=>v.Currency=="BTC"?v with{Frozen=1,Available=0}:v).ToArray()}};
        foreach(var mutant in variants)Assert.Throws<InvalidDataException>(()=>NavalBrokerValidation.ValidateBatch(baseline.Binding,mutant));
        var bootstrap=new NavalBrokerBootstrap(baseline.Binding,f.Service,f.Service);
        Assert.Throws<InvalidDataException>(()=>bootstrap.ConsiderBaseline(o,baseline.KeyScopeDigest));
        var wrong=new NavalBrokerBootstrap(baseline.Binding,f.Service,f.Service,new(baseline.Binding,Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(()=>wrong.ConsiderBaseline(o,baseline.KeyScopeDigest));
        var noScope=o with{FiniteAllocation=null};Assert.Throws<InvalidDataException>(()=>NavalBrokerValidation.ValidateBatch(baseline.Binding,noScope));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("extra-zero")]
    [InlineData("frozen")]
    [InlineData("liability")]
    [InlineData("future-source")]
    [InlineData("unexplained-quote-freeze")]
    public async Task Fresh_full_balance_anomaly_denies_risk_and_retains_STOP(string mutation)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",FeeRate=.002m};bool mutate=false;
        f.Mutate=(p,b)=>{MultiPeer(f,peer,p,b);if(!mutate||p!="/api/v5/account/balance")return;var rows=b["data"]![0]!["details"]!.AsArray();var btc=rows.Single(v=>v!["ccy"]!.GetValue<string>()=="BTC")!;
            if(mutation=="duplicate")rows.Add(btc.DeepClone());if(mutation=="missing")rows.Remove(btc);
            if(mutation=="extra-zero"){var extra=btc.DeepClone();extra["ccy"]="EUR";foreach(var k in new[]{"cashBal","availBal","eq"})extra[k]="0";rows.Add(extra);}
            if(mutation=="frozen"){btc["frozenBal"]="1";btc["availBal"]="0";}if(mutation=="liability")btc["liab"]="1";
            if(mutation=="unexplained-quote-freeze"){var q=rows.Single(v=>v!["ccy"]!.GetValue<string>()=="USDC")!;q["frozenBal"]="0.01";q["ordFrozen"]="0.01";q["availBal"]="1234.55";}
            if(mutation=="future-source")btc["uTime"]=f.Clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds().ToString();};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));mutate=true;
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.Equal("PREFLIGHT_RECONCILIATION_REFUSED",result);Assert.Empty(peer.Sends);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Armed);
    }
    private static void MultiPeer(OperationalPeer f,FiniteDemoPeer peer,string p,JsonNode b)
    {
        peer.Observe(p,b);OriginalSix(p,b);
        if(p=="/api/v5/account/instruments")
        {var ada=b["data"]!.AsArray().Single(v=>v!["baseCcy"]!.GetValue<string>()=="ADA")!;ada["groupId"]="9";}
        if(p=="/api/v5/account/trade-fee"&&f.Paths.Last().Contains("ADA-USDC",StringComparison.Ordinal))
        {var group=b["data"]![0]!["feeGroup"]![0]!;group["groupId"]="9";group["taker"]="-0.002";group["maker"]="-0.001";}
    }
}
