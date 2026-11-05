using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

public sealed class OkxStopPendingProtectionTests
{
    private static JsonNode Live()
    {
        var row=OkxOwnedExitDiagnosticTests.Oracle("stream-algo-schema.json","detail")["data"]![0]!.DeepClone();
        row["instType"]="SPOT";row["instId"]="ADA-USDC";row["algoId"]="889";row["algoClOrdId"]="OwnProtection1";
        row["side"]="sell";row["tdMode"]="cash";row["tradeQuoteCcy"]="USDC";row["ordType"]="oco";row["state"]="live";
        row["sz"]="0.0001998";row["slTriggerPx"]="100";row["tpTriggerPx"]="120";row["slOrdPx"]="-1";row["tpOrdPx"]="-1";
        row["slTriggerPxType"]="last";row["tpTriggerPxType"]="last";row["ordIdList"]=new JsonArray();return row;
    }
    private static OkxExecutionAlgoDto Parse(JsonNode row)
    {
        using var doc=JsonDocument.Parse(row.ToJsonString());
        var parser=typeof(OkxExecutionAlgoDto).Assembly.GetType("Apex.Adapters.Okx.Execution.OkxObservationJson")!.GetMethod("AlgoRow",BindingFlags.Static|BindingFlags.NonPublic)!;
        return (OkxExecutionAlgoDto)parser.Invoke(null,new object[]{doc.RootElement})!;
    }
    private static IReadOnlyList<string> Problems(JsonNode exact,JsonNode pending)
    {
        var method=typeof(Apex.Okx.DemoAcceptance.Program).Assembly.GetType("Apex.Okx.DemoAcceptance.FiniteStopRecovery")!.GetMethod("PendingOwnedProtectionProblems",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.NotNull(method);return (IReadOnlyList<string>)method.Invoke(null,new object[]{Parse(exact),Parse(pending)})!;
    }
    [Fact]
    public void Matching_live_no_child_common_economics_allow_distinct_endpoint_metadata()
    {
        var exact=Live();var pending=Live();pending["uTime"]="1724751378981";pending["last"]="110";pending["tag"]="observed-label";pending["sz"]="0.00019980";
        Assert.NotEqual(Parse(exact).PayloadDigest,Parse(pending).PayloadDigest);Assert.Empty(Problems(exact,pending));
    }
    [Theory][InlineData("live")][InlineData("canceled")]
    public void Any_matching_pending_after_exact_terminal_is_a_lifecycle_conflict(string pendingState)
    {
        var exact=Live();exact["state"]="canceled";var pending=Live();pending["state"]=pendingState;
        Assert.Contains("OWNED_PENDING_PROTECTION_LIFECYCLE_CONFLICT_NO_SELL",Problems(exact,pending));
    }
    [Theory]
    [InlineData("state","effective")][InlineData("state",null)]
    [InlineData("sz","0.0001")][InlineData("sz",null)]
    [InlineData("slTriggerPx","99")][InlineData("tpTriggerPx","121")]
    [InlineData("slOrdPx","99")][InlineData("tpOrdPx","121")]
    [InlineData("slTriggerPxType","mark")][InlineData("tpTriggerPxType","index")]
    [InlineData("side","buy")][InlineData("tdMode","cross")][InlineData("tradeQuoteCcy","USDT")]
    [InlineData("ordType","conditional")][InlineData("instType","MARGIN")]
    [InlineData("posSide","long")][InlineData("tgtCcy","base_ccy")][InlineData("ccy","USDT")]
    [InlineData("reduceOnly","true")][InlineData("isTradeBorrowMode","true")][InlineData("amendPxOnTriggerType","1")]
    [InlineData("advanceOrdType","iceberg")][InlineData("quickMgnType","auto_borrow")]
    [InlineData("closeFraction","1")][InlineData("triggerPx","100")][InlineData("triggerPxType","last")][InlineData("ordPx","100")]
    [InlineData("callbackRatio","0.01")][InlineData("callbackSpread","1")][InlineData("activePx","100")][InlineData("moveTriggerPx","100")]
    [InlineData("chaseType","distance")][InlineData("chaseVal","1")][InlineData("maxChaseType","distance")][InlineData("maxChaseVal","1")]
    [InlineData("failCode","1")][InlineData("actualSz","0.0001")][InlineData("actualPx","110")][InlineData("actualSide","sell")]
    [InlineData("triggerTime","1724751378981")][InlineData("clOrdId","Child1")][InlineData("ordId","999")]
    public void Same_identity_does_not_discard_selected_pending_lifecycle_or_economic_changes(string field,string? value)
    {
        var exact=Live();var pending=Live();if(value==null)pending.AsObject().Remove(field);else pending[field]=value;
        Assert.NotEmpty(Problems(exact,pending));
    }
    [Theory][InlineData("child")][InlineData("missing-child-list")][InlineData("sub-algo")][InlineData("linked-order")][InlineData("attachment")][InlineData("chase")]
    public void Same_identity_does_not_discard_pending_child_or_unknown_relation(string fault)
    {
        var exact=Live();var pending=Live();
        switch(fault)
        {
            case "child":pending["ordIdList"]=new JsonArray("999");break;
            case "missing-child-list":pending.AsObject().Remove("ordIdList");break;
            case "sub-algo":pending["subAlgoIdList"]=new JsonArray("999");break;
            case "linked-order":pending["linkedOrd"]=new JsonObject{["ordId"]="999"};break;
            case "attachment":pending["attachAlgoOrds"]=new JsonArray(new JsonObject{["attachAlgoClOrdId"]="Child1"});break;
            case "chase":pending["advChaseParams"]=new JsonArray(new JsonObject{["chaseType"]="distance",["chaseVal"]="1"});break;
        }
        Assert.Contains("OWNED_PENDING_PROTECTION_CHILD_OR_RELATION_UNPROVEN_NO_SELL",Problems(exact,pending));
    }
}
