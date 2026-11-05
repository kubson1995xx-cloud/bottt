using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxOwnCashFieldCompatibilityTests
{
    private sealed class Proof
    {
        internal JsonNode Exact=OkxOrderConsistencyOperandTests.Row(),History;
        internal JsonNode Fill;
        internal NavalBrokerBaseline Baseline;
        internal NavalBrokerOperationalState State;
        internal OkxOrderObservationOrigin HistoryOrigin=OkxOrderObservationOrigin.Recent;
        internal bool NoFills,ConflictingFill;
        internal Proof()
        {
            foreach(string n in new[]{"sz","accFillSz","fillSz"})Exact[n]="1";
            Exact["instId"]="SOL-EUR";Exact["ordId"]="777";Exact["clOrdId"]="OwnOrder1";Exact["posSide"]="net";Exact["tradeQuoteCcy"]="";Exact["state"]="filled";Exact["ordType"]="fok";Exact["side"]="buy";Exact["tdMode"]="cash";Exact["fee"]="-0.001";Exact["feeCcy"]="SOL";
            History=Exact.DeepClone();History["posSide"]="";History["tradeQuoteCcy"]="EUR";
            Fill=OkxOwnedExitDiagnosticTests.Oracle("recovery-schema.json","fills")["data"]![0]!.DeepClone();
            Fill["instId"]="SOL-EUR";Fill["ordId"]="777";Fill["clOrdId"]="OwnOrder1";Fill["tradeQuoteCcy"]="EUR";Fill["side"]="buy";Fill["fillSz"]="1";Fill["fillPx"]="110";Fill["fee"]="-0.001";Fill["feeCcy"]="SOL";
            var at=DateTimeOffset.FromUnixTimeMilliseconds(1708587373361);
            var account=new NavalAccountId("OKX",NavalEnvironment.OkxDemo,"9911112222","EUR");
            var binding=new NavalBrokerBinding(account,account.AccountId,"scope","config","credential","fingerprint","okx-cash-v1","storage");
            var observations=new NavalBrokerObservationBatch(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),1,at,1,at,
                new(account.AccountId,account.AccountId,"1","read_only,trade","192.0.2.1","0",false,false,false,"0","0"),
                Array.Empty<NavalBrokerBalance>(),new[]{new NavalInstrument("SOL-EUR","SOL","EUR",.000001m,.000001m,.01m)},null!,Array.Empty<NavalBrokerSurface>(),"catalog","fee");
            Baseline=new("digest",binding,1000,observations,"keyscope",at,1);
            byte[] body=Encoding.UTF8.GetBytes("{\"instId\":\"SOL-EUR\",\"clOrdId\":\"OwnOrder1\",\"tdMode\":\"cash\",\"side\":\"buy\",\"ordType\":\"fok\",\"sz\":\"1\",\"tradeQuoteCcy\":\"EUR\"}");
            State=new(){Intents=new[]{new NavalBrokerIntent("intent","reservation",NavalExecutionOperation.Submit,"SOL-EUR","OwnOrder1",null,body,"digest",at,Guid.NewGuid(),1,1,0,1,NavalExecutionStage.Dispatching,2)}};
        }
        internal OkxExecutionFillDto[] Fills()
        {
            if(NoFills)return Array.Empty<OkxExecutionFillDto>();
            var rows=new JsonArray(Fill.DeepClone());if(ConflictingFill){var changed=Fill.DeepClone();changed["fee"]="-0.002";rows.Add(changed);}
            return OkxObservationJson.Parse<OkxExecutionFillDto>(Encoding.UTF8.GetBytes(new JsonObject{["code"]="0",["msg"]="",["data"]=rows}.ToJsonString()),"/api/v5/trade/fills",100).ToArray();
        }
        internal string? Quote()
        {
            var a=OkxOrderConsistencyOperandTests.Parse(Exact);var b=OkxOrderConsistencyOperandTests.Parse(History,"/api/v5/trade/orders-history",true);
            return OkxOwnCashOrderCompatibility.EconomicQuote(Baseline,State,new[]{a,b},o=>ReferenceEquals(o,a)?OkxOrderObservationOrigin.Exact:HistoryOrigin,Fills());
        }
    }
    [Fact]
    public void EUR_own_request_history_and_individual_fill_project_without_changing_raw_or_diagnostics()
    {
        var p=new Proof();string before=p.Exact.ToJsonString();Assert.Equal("EUR",p.Quote());Assert.Equal(before,p.Exact.ToJsonString());
        var a=OkxOrderConsistencyOperandTests.Parse(p.Exact);var b=OkxOrderConsistencyOperandTests.Parse(p.History);
        Assert.False(OkxOrderConsistency.Equivalent(a,b));Assert.Equal(new[]{"PosSide","TradeQuoteCcy"},OkxOrderConsistency.Differences(a,b).Fields.Select(f=>f.Name));
        p.HistoryOrigin=OkxOrderObservationOrigin.Archive;Assert.Equal("EUR",p.Quote());
    }
    [Theory]
    [InlineData("long")][InlineData("short")][InlineData("position-missing")][InlineData("quote-missing")][InlineData("quote-wrong")]
    [InlineData("unowned")][InlineData("ambiguous-owner")][InlineData("undispatched")][InlineData("request-quote")][InlineData("request-missing")][InlineData("request-size")][InlineData("request-client")]
    [InlineData("fill-quote")][InlineData("fill-client")][InlineData("fill-fee")][InlineData("fill-size")][InlineData("retained-correction")]
    [InlineData("fee")][InlineData("quantity")][InlineData("state")][InlineData("attachment")][InlineData("children")][InlineData("config")][InlineData("equal-borrow")]
    [InlineData("pending")][InlineData("child")][InlineData("live")][InlineData("debt-mode")]
    [InlineData("no-fill")][InlineData("fill-conflict")][InlineData("no-explicit-history-quote")][InlineData("no-history-origin")]
    public void Missing_or_conflicting_context_never_invents_economic_quote(string fault)
    {
        var p=new Proof();Assert.Equal("EUR",p.Quote());var own=p.State.Intents.Single();
        switch(fault)
        {
            case "no-fill":p.NoFills=true;break;
            case "fill-conflict":p.ConflictingFill=true;break;
            case "no-explicit-history-quote":p.History["tradeQuoteCcy"]="";break;
            case "no-history-origin":p.HistoryOrigin=OkxOrderObservationOrigin.Exact;break;
            case "long":case "short":p.Exact["posSide"]=fault;break;
            case "position-missing":p.History.AsObject().Remove("posSide");break;
            case "quote-missing":p.Exact.AsObject().Remove("tradeQuoteCcy");break;
            case "quote-wrong":p.History["tradeQuoteCcy"]="USDT";break;
            case "unowned":p.State=p.State with{Intents=Array.Empty<NavalBrokerIntent>()};break;
            case "ambiguous-owner":p.State=p.State with{Intents=new[]{own,own with{Id="other"}}};break;
            case "undispatched":p.State=p.State with{Intents=new[]{own with{DispatchRevision=null}}};break;
            case "request-quote":case "request-missing":case "request-size":case "request-client":
                var body=JsonNode.Parse(own.Body)!;
                if(fault=="request-missing")body.AsObject().Remove("tradeQuoteCcy");
                else body[fault=="request-quote"?"tradeQuoteCcy":fault=="request-size"?"sz":"clOrdId"]=fault=="request-size"?"2":"OTHER";
                p.State=p.State with{Intents=new[]{own with{Body=Encoding.UTF8.GetBytes(body.ToJsonString())}}};break;
            case "fill-quote":p.Fill["tradeQuoteCcy"]="USDT";break;
            case "fill-client":p.Fill["clOrdId"]="Foreign1";break;
            case "fill-fee":p.Fill["fee"]="-0.002";break;
            case "fill-size":p.Fill["fillSz"]="2";break;
            case "retained-correction":var f=p.Fills().Single();p.State=p.State with{FillEvidence=new[]{new NavalBrokerRawFill(f.InstId,f.OrdId,f.ClOrdId,f.TradeId,f.BillId,f.Side!,f.FillSz.Value,f.FillPx.Value,-.002m,f.FeeCcy,f.TradeQuoteCcy,f.FillTime.Utc,f.PayloadDigest)}};break;
            case "fee":p.History["fee"]="-0.002";break;
            case "quantity":p.History["sz"]="2";break;
            case "state":p.History["state"]="live";break;
            case "attachment":p.History["attachAlgoOrds"]![0]!["attachAlgoId"]="999";break;
            case "children":p.History["linkedAlgoOrd"]!["algoId"]="999";break;
            case "config":p.History["quickMgnType"]="auto_borrow";break;
            case "equal-borrow":p.Exact["quickMgnType"]="auto_borrow";p.History["quickMgnType"]="auto_borrow";break;
            case "pending":p.HistoryOrigin=OkxOrderObservationOrigin.Pending;break;
            case "child":p.HistoryOrigin=OkxOrderObservationOrigin.Child;break;
            case "live":p.Baseline=p.Baseline with{Binding=p.Baseline.Binding with{Account=p.Baseline.Binding.Account with{Environment=NavalEnvironment.Live}}};break;
            case "debt-mode":p.Baseline=p.Baseline with{Observations=p.Baseline.Observations with{Account=p.Baseline.Observations.Account with{Borrow=true}}};break;
        }
        Assert.Null(p.Quote());
    }
}
