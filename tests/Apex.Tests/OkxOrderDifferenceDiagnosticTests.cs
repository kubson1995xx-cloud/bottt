using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

public sealed class OkxOrderDifferenceDiagnosticTests
{
    internal static JsonElement Diagnose(OkxExecutionOrderDto a,OkxExecutionOrderDto b)
    {
        var method=typeof(OkxOrderConsistency).GetMethod("Differences",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.True(method!=null,"BOUNDED_SAME_PREDICATE_DIFFERENCES_REQUIRED");
        return JsonSerializer.SerializeToElement(method!.Invoke(null,new object[]{a,b}));
    }
    [Fact]
    public System.Threading.Tasks.Task Own_cash_accepted_context_still_diagnoses_raw_differences_without_writes()=>NativeDiagnostic(true);
    [Fact]
    public System.Threading.Tasks.Task Native_DIAGNOSE_is_STOP_unarmed_and_does_not_prepare_or_write()=>NativeDiagnostic(false);
    private static async System.Threading.Tasks.Task NativeDiagnostic(bool cash)
    {
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,cash?"mixed-cash-diagnose":"mixed-diagnose"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");
            await child.Command("DIAGNOSE","DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED");
            await child.Command("CONNECT","CONNECTED_STOP");
            await child.Command("DIAGNOSE","ORDER_DIFFERENCE");
            await child.Expect("DIAGNOSE_COMPLETE");
            await child.Command("STATUS","armed=False");
            await child.Command("STOP","STOP_DRAINED_UNARMED");
            await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);
            Assert.Contains("NATIVE_DIAGNOSE_PASS noWrites=True noNewIds=True unchangedOwner=True",child.Log);
            Assert.Contains("Exact",child.Log);Assert.Contains("Recent",child.Log);
            if(cash){Assert.Contains("PosSide",child.Log);Assert.Contains("TradeQuoteCcy",child.Log);}
            Assert.DoesNotContain("SENSITIVE_PROVIDER_SENTINEL",child.Log);
        }
        System.IO.Directory.Delete(root,true);
    }
    [Fact]
    public void Complete_operand_inventory_names_match_predicate_in_both_directions()
    {
        foreach(var operand in OkxOrderConsistencyOperandTests.RootOperands())
        {
            string name=(string)operand[0];var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();b[name]=(string)operand[1];
            var left=OkxOrderConsistencyOperandTests.Parse(a);var right=OkxOrderConsistencyOperandTests.Parse(b);
            Assert.False(OkxOrderConsistency.Equivalent(left,right));
            string expected=new[]{"pxUsd","pxVol","pxType","stpId","lever","cancelSourceReason","quickMgnType"}.Contains(name)?name:char.ToUpperInvariant(name[0])+name.Substring(1);
            foreach(var report in new[]{Diagnose(left,right),Diagnose(right,left)})
                Assert.Equal(expected,Assert.Single(report.GetProperty("Fields").EnumerateArray()).GetProperty("Name").GetString());
        }
        foreach(var operand in OkxOrderConsistencyOperandTests.AttachmentOperands().Concat(new[]{new object[]{"percent","1"}}))
        {
            string name=(string)operand[0];var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();b["attachAlgoOrds"]![0]![name]=(string)operand[1];
            var left=OkxOrderConsistencyOperandTests.Parse(a,retrospective:true);var right=OkxOrderConsistencyOperandTests.Parse(b,retrospective:true);
            Assert.False(OkxOrderConsistency.Equivalent(left,right));
            string expected="AttachAlgoOrds[]."+(name is "percent" or "failReason"?name:char.ToUpperInvariant(name[0])+name.Substring(1));
            Assert.Equal(expected,Assert.Single(Diagnose(left,right).GetProperty("Fields").EnumerateArray()).GetProperty("Name").GetString());
        }
        var x=OkxOrderConsistencyOperandTests.Row();var y=x.DeepClone();y["slippage"]="PRIVATE_CONFIGURATION";
        Assert.Equal("slippage",Assert.Single(Diagnose(OkxOrderConsistencyOperandTests.Parse(x,retrospective:true),OkxOrderConsistencyOperandTests.Parse(y,retrospective:true)).GetProperty("Fields").EnumerateArray()).GetProperty("Name").GetString());
    }
    [Theory][InlineData("px")][InlineData("feeCcy")][InlineData("fillTime")][InlineData("quickMgnType")]
    public void Missing_empty_known_classification_never_exports_values(string name)
    {
        var missing=OkxOrderConsistencyOperandTests.Row();missing.AsObject().Remove(name);
        var empty=missing.DeepClone();empty[name]="";var known=missing.DeepClone();known[name]=name=="fillTime"?"1708587373363":name=="px"?"987.654":"PRIVATE_VALUE";
        var m=OkxOrderConsistencyOperandTests.Parse(missing);var e=OkxOrderConsistencyOperandTests.Parse(empty);var k=OkxOrderConsistencyOperandTests.Parse(known);
        var first=Assert.Single(Diagnose(m,e).GetProperty("Fields").EnumerateArray());
        Assert.Equal("Missing",first.GetProperty("LeftKind").GetString());Assert.Equal("Empty",first.GetProperty("RightKind").GetString());
        var report=Diagnose(e,k);var second=Assert.Single(report.GetProperty("Fields").EnumerateArray());
        Assert.Equal("Empty",second.GetProperty("LeftKind").GetString());Assert.Equal("Known",second.GetProperty("RightKind").GetString());
        Assert.DoesNotContain("PRIVATE_VALUE",report.ToString());Assert.DoesNotContain("987.654",report.ToString());Assert.DoesNotContain("1708587373363",report.ToString());
    }
    [Fact]
    public void Relationship_presence_cardinality_and_linked_identity_are_safe()
    {
        foreach(string name in new[]{"attachAlgoOrds","linkedAlgoOrd"})
        {
            var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();b.AsObject().Remove(name);
            var field=Assert.Single(Diagnose(OkxOrderConsistencyOperandTests.Parse(a),OkxOrderConsistencyOperandTests.Parse(b)).GetProperty("Fields").EnumerateArray());
            Assert.Equal("Missing",field.GetProperty("RightKind").GetString());
            Assert.Equal(name=="attachAlgoOrds"?"List":"Object",field.GetProperty("LeftKind").GetString());
        }
        var x=OkxOrderConsistencyOperandTests.Row();var y=x.DeepClone();y["attachAlgoOrds"]=new System.Text.Json.Nodes.JsonArray();
        var counts=Assert.Single(Diagnose(OkxOrderConsistencyOperandTests.Parse(x),OkxOrderConsistencyOperandTests.Parse(y)).GetProperty("Fields").EnumerateArray());
        Assert.Equal(1,counts.GetProperty("LeftCount").GetInt32());Assert.Equal(0,counts.GetProperty("RightCount").GetInt32());
        y=x.DeepClone();y["linkedAlgoOrd"]!["algoId"]="999999";
        Assert.Equal("LinkedAlgoOrd.AlgoId",Assert.Single(Diagnose(OkxOrderConsistencyOperandTests.Parse(x),OkxOrderConsistencyOperandTests.Parse(y)).GetProperty("Fields").EnumerateArray()).GetProperty("Name").GetString());
    }
    [Fact]
    public void Truncation_is_explicit_and_unknown_extra_keys_cannot_become_names()
    {
        var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();
        foreach(var operand in OkxOrderConsistencyOperandTests.RootOperands())b[(string)operand[0]]=(string)operand[1];
        var report=Diagnose(OkxOrderConsistencyOperandTests.Parse(a),OkxOrderConsistencyOperandTests.Parse(b));
        Assert.Equal(16,report.GetProperty("Fields").GetArrayLength());Assert.True(report.GetProperty("Truncated").GetBoolean());
        var left=OkxOrderConsistencyOperandTests.Parse(a);var right=left with{ConsistencyExtras=left.ConsistencyExtras!.Add("PRIVATE_PROVIDER_KEY","PRIVATE_PROVIDER_VALUE")};
        var opaque=Diagnose(left,right);Assert.False(OkxOrderConsistency.Equivalent(left,right));
        Assert.Equal("ConsistencyExtras",Assert.Single(opaque.GetProperty("Fields").EnumerateArray()).GetProperty("Name").GetString());
        Assert.DoesNotContain("PRIVATE_PROVIDER",opaque.ToString());
        right=left with{ConsistencyExtras=null};Assert.NotEmpty(Diagnose(right,right).GetProperty("Fields").EnumerateArray());Assert.False(OkxOrderConsistency.Equivalent(right,right));
    }
    [Fact]
    public void Collector_output_bounds_and_origin_inventory_are_explicit()
    {
        var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();b["fee"]="-987.654";
        var left=OkxOrderConsistencyOperandTests.Parse(a);var right=OkxOrderConsistencyOperandTests.Parse(b);
        var result=new Apex.Desktop.Okx.OkxOrderDiagnostic();
        var origins=Enum.GetValues<Apex.Desktop.Okx.OkxOrderObservationOrigin>();
        Assert.Equal(new[]{"Exact","Recent","Archive","Pending","Child"},origins.Select(o=>o.ToString()));
        for(int n=0;n<33;n++)result.Add(left,right,origins[n%origins.Length],origins[(n+1)%origins.Length]);
        Assert.Equal(32,result.Disagreements.Count);Assert.True(result.Truncated);
        Assert.All(result.Disagreements,d=>Assert.Single(d.Difference.Fields));
    }
    [Fact]
    public void Unchanged_and_approved_lexical_positive_have_no_differences()
    {
        var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();b["sz"]="100.000";b["tag"]="PRIVATE_DESCRIPTION";
        var left=OkxOrderConsistencyOperandTests.Parse(a);var right=OkxOrderConsistencyOperandTests.Parse(b);
        Assert.True(OkxOrderConsistency.Equivalent(left,right));Assert.Empty(Diagnose(left,right).GetProperty("Fields").EnumerateArray());
        Assert.Empty(Diagnose(left,left).GetProperty("Fields").EnumerateArray());
    }
    [Fact]
    public void Single_difference_reports_only_trusted_name_and_classes()
    {
        var a=OkxOrderConsistencyOperandTests.Row();var b=a.DeepClone();b["fee"]="-987.654";
        var report=Diagnose(OkxOrderConsistencyOperandTests.Parse(a),OkxOrderConsistencyOperandTests.Parse(b));
        var item=Assert.Single(report.GetProperty("Fields").EnumerateArray());
        Assert.Equal("Fee",item.GetProperty("Name").GetString());
        Assert.DoesNotContain("987.654",report.ToString());
        Assert.False(report.GetProperty("Truncated").GetBoolean());
    }
}
