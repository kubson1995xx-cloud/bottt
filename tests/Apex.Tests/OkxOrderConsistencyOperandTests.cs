using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

public sealed class OkxOrderConsistencyOperandTests
{
    internal static JsonNode Row()
    {
        var row=OkxOwnedExitDiagnosticTests.Oracle("recovery-schema.json","order")["data"]![0]!.DeepClone();
        row["clOrdId"]="OwnOrder1";row["outcome"]="";
        row["attachAlgoOrds"]=new JsonArray(new JsonObject{
            ["attachAlgoId"]="888",["attachAlgoClOrdId"]="OwnProtection1",["tpOrdKind"]="condition",
            ["tpTriggerPx"]="120",["tpTriggerRatio"]="",["tpTriggerPxType"]="last",["tpOrdPx"]="-1",
            ["slTriggerPx"]="100",["slTriggerRatio"]="",["slTriggerPxType"]="last",["slOrdPx"]="-1",
            ["sz"]="0.001",["amendPxOnTriggerType"]="0",["callbackRatio"]="",["callbackSpread"]="",["activePx"]="",["failCode"]="",["failReason"]=""});
        return row;
    }
    internal static OkxExecutionOrderDto Parse(JsonNode row,string endpoint="/api/v5/trade/order",bool retrospective=false)=>
        OkxObservationJson.Parse<OkxExecutionOrderDto>(Encoding.UTF8.GetBytes(new JsonObject{["code"]="0",["msg"]="",["data"]=new JsonArray(row.DeepClone())}.ToJsonString()),endpoint,endpoint=="/api/v5/trade/order"?null:100,null,retrospective).Single();
    public static IEnumerable<object[]> RootOperands()
    {
        foreach(string n in new[]{"px","sz","pnl","accFillSz","fillPx","fillSz","avgPx","tpTriggerPx","tpOrdPx","slTriggerPx","slOrdPx","fee","rebate"})yield return new object[]{n,"987.654"};
        foreach(string n in new[]{"cTime","uTime","fillTime"})yield return new object[]{n,"1708587373363"};
        foreach(string n in new[]{"tgtCcy","ccy","ordType","side","posSide","tdMode","state","stpMode","attachAlgoClOrdId","tpTriggerPxType","slTriggerPxType","feeCcy","rebateCcy","source","category","reduceOnly","isTpLimit","cancelSource","algoClOrdId","tradeQuoteCcy","outcome","pxUsd","pxVol","pxType","stpId","lever","cancelSourceReason","quickMgnType"})yield return new object[]{n,"changed"};
        yield return new object[]{"instType","MARGIN"};yield return new object[]{"instId","SOL-USDT"};yield return new object[]{"ordId","999"};yield return new object[]{"clOrdId","OtherOrder1"};yield return new object[]{"algoId","999"};yield return new object[]{"tradeId","999"};
    }
    [Theory][MemberData(nameof(RootOperands))]
    public void Every_root_identity_economic_lifecycle_configuration_operand_disagrees(string name,string value)
    {
        var a=Row();var b=a.DeepClone();b[name]=value;
        Assert.False(OkxOrderConsistency.Equivalent(Parse(a),Parse(b)),"Uncompared root operand: "+name);
        Assert.False(OkxOrderConsistency.Equivalent(Parse(b),Parse(a)));
    }
    public static IEnumerable<object[]> AttachmentOperands()
    {
        foreach(string n in new[]{"tpTriggerPx","tpTriggerRatio","tpOrdPx","slTriggerPx","slTriggerRatio","slOrdPx","sz","callbackRatio","callbackSpread","activePx"})yield return new object[]{n,"987.654"};
        foreach(string n in new[]{"attachAlgoId","attachAlgoClOrdId","tpOrdKind","tpTriggerPxType","slTriggerPxType","amendPxOnTriggerType","failCode","failReason"})yield return new object[]{n,"changed"};
    }
    [Theory][MemberData(nameof(AttachmentOperands))]
    public void Every_attachment_identity_SLTP_quantity_failure_or_advanced_setting_disagrees(string name,string value)
    {
        var a=Row();var b=a.DeepClone();b["attachAlgoOrds"]![0]![name]=value;
        Assert.False(OkxOrderConsistency.Equivalent(Parse(a),Parse(b)),"Uncompared attachment operand: "+name);
    }
    [Theory][InlineData("slippage")][InlineData("percent")]
    public void Retrospective_opaque_configuration_is_retained_not_guessed_harmless(string name)
    {
        var a=Row();var b=a.DeepClone();
        if(name=="percent"){a["attachAlgoOrds"]![0]![name]="";b["attachAlgoOrds"]![0]![name]="1";}
        else {a[name]="";b[name]="0.01";}
        Assert.False(OkxOrderConsistency.Equivalent(Parse(a,retrospective:true),Parse(b,retrospective:true)));
    }
    [Theory]
    [InlineData("px")][InlineData("fee")][InlineData("feeCcy")][InlineData("fillTime")][InlineData("quickMgnType")]
    [InlineData("attachAlgoOrds")][InlineData("linkedAlgoOrd")]
    public void Missing_is_not_empty_zero_or_an_explicit_empty_relation(string name)
    {
        var a=Row();var b=a.DeepClone();b.AsObject().Remove(name);
        Assert.False(OkxOrderConsistency.Equivalent(Parse(a),Parse(b)));
    }
    [Theory][InlineData("missing")][InlineData("empty")][InlineData("zero")]
    public void Unavailable_numeric_operands_never_equal_known_zero(string unavailable)
    {
        var a=Row();var b=a.DeepClone();a["px"]="0";
        if(unavailable=="missing")b.AsObject().Remove("px");else b["px"]=unavailable=="zero"?"0.0000000000000000000000000001":"";
        Assert.False(OkxOrderConsistency.Equivalent(Parse(a),Parse(b)));
    }
    [Theory][InlineData("child-id")][InlineData("linked-id")][InlineData("new-attachment")][InlineData("missing-attachment")][InlineData("missing-percent")]
    public void Child_attachment_cardinality_and_presence_are_not_identity_only_proof(string mutation)
    {
        var a=Row();var b=a.DeepClone();
        switch(mutation)
        {
            case "child-id":b["algoId"]="999";break;
            case "linked-id":b["linkedAlgoOrd"]!["algoId"]="999";break;
            case "new-attachment":b["attachAlgoOrds"]!.AsArray().Add(b["attachAlgoOrds"]![0]!.DeepClone());break;
            case "missing-attachment":b["attachAlgoOrds"]=new JsonArray();break;
            case "missing-percent":a["attachAlgoOrds"]![0]!["percent"]="";break;
        }
        Assert.False(OkxOrderConsistency.Equivalent(Parse(a,retrospective:true),Parse(b,retrospective:true)));
    }
    [Fact]
    public void Exact_decimal_format_and_label_are_the_only_changed_operands_in_positive_and_raw_hashes_survive()
    {
        var a=Row();var b=a.DeepClone();b["sz"]="100.000";b["fee"]="-0.000001928340";b["tag"]="history-label";
        b["attachAlgoOrds"]![0]!["sz"]="0.00100";b["attachAlgoOrds"]![0]!["slTriggerPx"]="100.0";
        var first=Parse(a);var next=Parse(b,"/api/v5/trade/orders-history");
        Assert.NotEqual(first.PayloadDigest,next.PayloadDigest);Assert.True(OkxOrderConsistency.Equivalent(first,next));
        Assert.Equal("100.000",next.Sz.Raw);Assert.Equal("-0.000001928340",next.Fee.Raw);Assert.Equal("history-label",next.Tag);
        using var raw=JsonDocument.Parse(b.ToJsonString());Assert.Equal(OkxObservationJson.PayloadDigest(raw.RootElement),next.PayloadDigest);
    }
    [Theory][InlineData("/api/v5/trade/order")][InlineData("/api/v5/trade/orders-pending")][InlineData("/api/v5/trade/orders-history")][InlineData("/api/v5/trade/orders-history-archive")]
    public void Unknown_fields_and_bad_kinds_still_refuse_at_each_endpoint(string endpoint)
    {
        var unknown=Row();unknown["unapprovedConfiguration"]="";Assert.ThrowsAny<Exception>(()=>Parse(unknown,endpoint,true));
        var bad=Row();bad["fee"]=1;Assert.ThrowsAny<Exception>(()=>Parse(bad,endpoint,true));
        var imprecise=Row();imprecise["fee"]="0.00000000000000000000000000001";Assert.ThrowsAny<Exception>(()=>Parse(imprecise,endpoint,true));
    }
    [Fact]
    public void Closed_parser_schema_inventory_is_fully_covered_by_operand_tests()
    {
        var schemas=(System.Collections.IDictionary)typeof(OkxObservationJson).GetField("RecoverySchemas",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        foreach(string endpoint in new[]{"/api/v5/trade/order","/api/v5/trade/orders-pending","/api/v5/trade/orders-history","/api/v5/trade/orders-history-archive"})
        {
            var schema=schemas[endpoint]!;string strings=(string)schema.GetType().GetProperty("Strings")!.GetValue(schema)!;
            Assert.Equal(strings.Split(' ').OrderBy(n=>n),RootOperands().Select(v=>(string)v[0]).Append("tag").OrderBy(n=>n));
        }
        string attachment=(string)typeof(OkxObservationJson).GetField("OrderAttachmentStrings",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Assert.Equal(attachment.Split(' ').OrderBy(n=>n),AttachmentOperands().Select(v=>(string)v[0]).OrderBy(n=>n));
        // The separately admitted retrospective slippage and percent operands are
        // exercised by Retrospective_opaque_configuration_is_retained_not_guessed_harmless.
    }
    [Fact]
    public void DTO_property_inventory_requires_an_explicit_comparison_decision()
    {
        var expected="InstType InstId OrdId ClOrdId Px Sz Pnl AccFillSz FillPx FillSz AvgPx TpTriggerPx TpOrdPx SlTriggerPx SlOrdPx Fee Rebate FillTime UTime CTime TgtCcy Ccy Tag OrdType Side PosSide TdMode TradeId State StpMode AttachAlgoClOrdId TpTriggerPxType SlTriggerPxType FeeCcy RebateCcy Source Category ReduceOnly IsTpLimit CancelSource AlgoClOrdId AlgoId TradeQuoteCcy Outcome AttachAlgoOrds LinkedAlgoOrd PayloadDigest".Split(' ').OrderBy(n=>n);
        Assert.Equal(expected,typeof(OkxExecutionOrderDto).GetProperties().Select(p=>p.Name).OrderBy(n=>n));
    }
}
