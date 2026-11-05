using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Apex.Tests;

public sealed class OkxAdvisoryTests
{
    internal static readonly DateTimeOffset Now = new(2026,11,5,12,0,0,TimeSpan.Zero);
    internal static byte[] Report(string tool="vibe-trading", string recommendation="BUY", Action<Dictionary<string,object>>? edit=null)
    {
        var fields=new Dictionary<string,object>{["Schema"]="apex-advisory-v1",["Status"]="ok",["Tool"]=tool,
            ["Instrument"]="SOL-EUR",["QuoteCurrency"]="EUR",["AsOfUtc"]="2026-11-05T00:00:00Z",["ExpiresUtc"]="2026-11-06T00:00:00Z",
            ["DatasetSha256"]=new string('a',64),["Provider"]="openai-codex",["Model"]="gpt-6.1-sol",["Recommendation"]=recommendation,
            ["ExecutionAllowed"]=false,["EvidenceSources"]=new[]{"https://www.okx.com/markets/spot-info/sol-eur"},["Summary"]="Synthetic public research test only.",
            ["SourceCommit"]=tool=="vibe-trading"?"8172827ec537146920e112a9952e8e7e063e218b":"9fa1196fffbcfd9de5146d2dd2869df84ab99838"};
        edit?.Invoke(fields);return JsonSerializer.SerializeToUtf8Bytes(fields);
    }
    internal static JsonElement Compare(byte[] left,byte[] right)
    {
        var type=typeof(Apex.Desktop.Okx.OkxConfiguration).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisoryComparison");
        Assert.True(type!=null,"Missing read-only advisory consumer");
        var method=type!.GetMethod("Compare",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result=method!.Invoke(null,new object[]{left,right,Now});
        return JsonSerializer.SerializeToElement(result,result!.GetType());
    }
    public static IEnumerable<object[]> InvalidReports()
    {
        var original=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Report())!;
        foreach(var key in original.Keys)
        {
            yield return new object[]{"missing_"+key,Report(edit:f=>f.Remove(key))};
            var text=Encoding.UTF8.GetString(Report());
            yield return new object[]{"duplicate_"+key,Encoding.UTF8.GetBytes(text.Insert(1,JsonSerializer.Serialize(key)+":"+original[key].GetRawText()+","))};
            yield return new object[]{"null_"+key,Report(edit:f=>f[key]=null!)};
        }
        foreach(var pair in new (string Key,object Value)[]{
            ("Schema","unknown"),("Status","good"),("Tool","tradingagents"),("Instrument","SOL-USD"),("Instrument","sol-eur"),("Instrument","SOL-EUR/../x"),
            ("QuoteCurrency","USD"),("QuoteCurrency","USDT"),("QuoteCurrency","eur"),("Provider","openrouter"),("Model","alias"),
            ("SourceCommit","9fa1196fffbcfd9de5146d2dd2869df84ab99838"),("DatasetSha256",new string('A',64)),("DatasetSha256","abc"),
            ("Recommendation","buy"),("Recommendation","HOLD"),("ExecutionAllowed",true),("ExecutionAllowed","false"),
            ("AsOfUtc","2026-11-05T12:00:01Z"),("AsOfUtc","2026-11-05T11:44:59Z"),("AsOfUtc","2026-11-05T11:59:00+00:00"),
            ("AsOfUtc","2026-02-30T11:59:00Z"),("ExpiresUtc","2026-11-05T12:00:00Z"),("ExpiresUtc","2026-11-05T11:58:00Z"),
            ("ExpiresUtc","2026-11-05T12:15:00Z"),("Summary",new string('x',1001)),("Summary","line\nprivate"),
            ("EvidenceSources",new[]{"file:///etc/passwd"}),("EvidenceSources",new[]{"http://example.com"}),
            ("EvidenceSources",new[]{"https://user:pass@example.com"}),("EvidenceSources",new[]{"https://example.com/?token=secret"}),
            ("EvidenceSources",new string[9]),("EvidenceSources",new[]{"https://example.com/"+new string('x',1024)})})
            yield return new object[]{pair.Key+"_"+pair.Value.GetType().Name+"_"+Convert.ToString(pair.Value),Report(edit:f=>f[pair.Key]=pair.Value)};
        yield return new object[]{"unknown",Report(edit:f=>f["PrivateBalance"]=1)};
        yield return new object[]{"oversized",Encoding.UTF8.GetBytes(new string(' ',16385))};
        yield return new object[]{"nonfinite",Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Report()).Replace("false","NaN"))};
        yield return new object[]{"infinite",Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Report()).Replace("false","1e9999"))};
        yield return new object[]{"trailing",Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Report())+"{}")};
        yield return new object[]{"array",Encoding.UTF8.GetBytes("[]")};
        yield return new object[]{"empty",Array.Empty<byte>()};
    }
    [Theory]
    [MemberData(nameof(InvalidReports))]
    public void Malformed_untrusted_report_is_unavailable_and_cannot_allow_entry(string name,byte[] bad)
    {
        _=name;
        var result=Compare(bad,Report("tradingagents"));
        Assert.Equal("UNAVAILABLE",result.GetProperty("Comparison").GetString());
        Assert.Equal("WOULD_VETO_ENTRY",result.GetProperty("PlannedEntryFilter").GetString());
        Assert.Equal("INVALID_VIBE",result.GetProperty("Reason").GetString());
        Assert.False(result.GetProperty("BlocksMandatoryExit").GetBoolean());
    }
    [Theory]
    [InlineData("DatasetSha256","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","PAIR_DATASET_MISMATCH")]
    [InlineData("AsOfUtc","2026-11-05T00:01:01Z","INVALID_TRADINGAGENTS")]
    [InlineData("Status","unavailable","TOOL_UNAVAILABLE")]
    public void Incompatible_or_unavailable_pair_cannot_describe_consensus(string key,string value,string reason)
    {
        var result=Compare(Report(),Report("tradingagents",edit:f=>f[key]=value));
        Assert.Equal("UNAVAILABLE",result.GetProperty("Comparison").GetString());
        Assert.Equal("WOULD_VETO_ENTRY",result.GetProperty("PlannedEntryFilter").GetString());
        Assert.Equal(reason,result.GetProperty("Reason").GetString());
    }
    [Theory]
    [InlineData("BUY","SELL","DISAGREE","WOULD_VETO_ENTRY")]
    [InlineData("SELL","SELL","AGREE_SELL","WOULD_VETO_ENTRY")]
    [InlineData("WAIT","WAIT","AGREE_WAIT","WOULD_VETO_ENTRY")]
    [InlineData("WAIT","BUY","DISAGREE","WOULD_VETO_ENTRY")]
    [InlineData("BUY","BUY","AGREE_BUY","WOULD_ALLOW_ENTRY")]
    public void Intended_new_entry_filter_is_only_a_descriptive_projection(string left,string right,string comparison,string decision)
    {
        var result=Compare(Report(recommendation:left),Report("tradingagents",right));
        Assert.Equal(comparison,result.GetProperty("Comparison").GetString());
        Assert.Equal(decision,result.GetProperty("PlannedEntryFilter").GetString());
        Assert.False(result.GetProperty("AutomaticActivation").GetBoolean());
        Assert.False(result.GetProperty("ExecutionAllowed").GetBoolean());
        Assert.False(result.GetProperty("BlocksMandatoryExit").GetBoolean());
    }
    [Theory]
    [InlineData("AsOfUtc","2026-11-05T00:00:00Z")]
    [InlineData("AsOfUtc","2026-11-05T00:00:00.0000000Z")]
    [InlineData("Summary","")]
    public void Compatible_boundary_controls_remain_accepted(string key,string value)
    {
        var result=Compare(Report(),Report("tradingagents",edit:f=>f[key]=value));
        Assert.Equal("AGREE_BUY",result.GetProperty("Comparison").GetString());
    }
    [Theory]
    [InlineData("QuoteCurrency","USDT")]
    [InlineData("Instrument","SOL-USD")]
    [InlineData("SourceCommit","8172827ec537146920e112a9952e8e7e063e218b")]
    public void Second_tool_has_independent_route_currency_and_source_checks(string key,string value)
    {
        var result=Compare(Report(),Report("tradingagents",edit:f=>f[key]=value));
        Assert.Equal("INVALID_TRADINGAGENTS",result.GetProperty("Reason").GetString());
    }
    [Fact]
    public void Good_pair_compares_without_granting_trading_authority()
    {
        var result=Compare(Report(),Report("tradingagents"));
        Assert.Equal("AGREE_BUY",result.GetProperty("Comparison").GetString());
        Assert.Equal("WOULD_ALLOW_ENTRY",result.GetProperty("PlannedEntryFilter").GetString());
        Assert.Equal("SHADOW_ONLY",result.GetProperty("Mode").GetString());
        Assert.False(result.GetProperty("AutomaticActivation").GetBoolean());
        Assert.False(result.GetProperty("ExecutionAllowed").GetBoolean());
        Assert.False(result.GetProperty("BlocksMandatoryExit").GetBoolean());
    }
}
