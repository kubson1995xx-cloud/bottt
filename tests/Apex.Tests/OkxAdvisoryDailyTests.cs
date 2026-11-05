using System;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Apex.Tests;

public sealed class OkxAdvisoryDailyTests
{
    // Actual producer's D1 clock shape, with synthetic public research content.
    // A generation-time / 15-minute consumer rejects legitimate daily research.
    private static byte[] Daily(string tool="vibe-trading",string asOf="2026-11-05T00:00:00Z",string expires="2026-11-06T00:00:00Z") =>
        OkxAdvisoryTests.Report(tool,edit:f=>{f["AsOfUtc"]=asOf;f["ExpiresUtc"]=expires;});
    private static JsonElement At(byte[] left,byte[] right,string now)
    {
        var type=typeof(Apex.Desktop.Okx.OkxConfiguration).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisoryComparison")!;
        var result=type.GetMethod("Compare",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{left,right,
            DateTimeOffset.Parse(now,CultureInfo.InvariantCulture)});
        return JsonSerializer.SerializeToElement(result,result!.GetType());
    }
    [Theory]
    [InlineData("2026-11-05T00:00:00Z")]
    [InlineData("2026-11-05T00:00:00.0000001Z")]
    [InlineData("2026-11-05T00:15:00.0000001Z")]
    [InlineData("2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T23:59:59.9999999Z")]
    [InlineData("2026-11-06T00:30:00+01:00")]
    public void Completed_D1_current_UTC_day_is_usable_only_as_shadow_research(string now)
    {
        var result=At(Daily(),Daily("tradingagents"),now);
        Assert.Equal("AGREE_BUY",result.GetProperty("Comparison").GetString());
        Assert.Equal("WOULD_ALLOW_ENTRY",result.GetProperty("PlannedEntryFilter").GetString());
        Assert.Equal("SHADOW_ONLY",result.GetProperty("Mode").GetString());
        Assert.False(result.GetProperty("AutomaticActivation").GetBoolean());
        Assert.False(result.GetProperty("ExecutionAllowed").GetBoolean());
        Assert.False(result.GetProperty("BlocksMandatoryExit").GetBoolean());
    }
    [Theory]
    [InlineData("2026-11-04T00:00:00Z","2026-11-05T00:00:00Z","2026-11-05T00:00:00Z")]
    [InlineData("2026-11-04T00:00:00Z","2026-11-06T00:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-06T00:00:00Z","2026-11-07T00:00:00Z","2026-11-05T23:59:59.9999999Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-06T00:00:00Z","2026-11-04T23:59:59.9999999Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-06T00:00:00Z","2026-11-06T00:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-06T00:00:00Z","2026-11-06T00:00:00.0000001Z")]
    [InlineData("2026-11-05T12:00:00Z","2026-11-06T12:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T11:59:00Z","2026-11-05T12:09:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:00.0000001Z","2026-11-06T00:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:01Z","2026-11-06T00:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-06T01:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-07T00:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-05T23:59:59.9999999Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-06T00:00:00.0000001Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z","2026-11-05T00:00:00Z","2026-11-05T00:00:00Z")]
    [InlineData("2026-11-05T00:00:00+00:00","2026-11-06T00:00:00Z","2026-11-05T12:00:00Z")]
    [InlineData("2026-02-30T00:00:00Z","2026-11-06T00:00:00Z","2026-11-05T12:00:00Z")]
    public void Intraday_wrong_day_future_old_or_arbitrary_expiry_is_rejected_for_each_tool(string asOf,string expires,string now)
    {
        // Keep the opposite report valid for the observed UTC day to reach each parser independently.
        var clock=DateTimeOffset.Parse(now,CultureInfo.InvariantCulture);
        string day=clock.UtcDateTime.Date.ToString("yyyy-MM-dd'T'00:00:00'Z'",CultureInfo.InvariantCulture);
        string next=clock.UtcDateTime.Date.AddDays(1).ToString("yyyy-MM-dd'T'00:00:00'Z'",CultureInfo.InvariantCulture);
        foreach(bool badLeft in new[]{true,false})
        {
            var result=At(Daily(asOf:badLeft?asOf:day,expires:badLeft?expires:next),
                Daily("tradingagents",badLeft?day:asOf,badLeft?next:expires),now);
            Assert.Equal("UNAVAILABLE",result.GetProperty("Comparison").GetString());
            Assert.Equal(badLeft?"INVALID_VIBE":"INVALID_TRADINGAGENTS",result.GetProperty("Reason").GetString());
            Assert.Equal("WOULD_VETO_ENTRY",result.GetProperty("PlannedEntryFilter").GetString());
            Assert.False(result.GetProperty("AutomaticActivation").GetBoolean());
            Assert.False(result.GetProperty("ExecutionAllowed").GetBoolean());
            Assert.False(result.GetProperty("BlocksMandatoryExit").GetBoolean());
        }
    }
    [Fact]
    public void Legacy_fifteen_minute_intraday_pair_is_not_daily_research()
    {
        var result=At(Daily(asOf:"2026-11-05T11:59:00Z",expires:"2026-11-05T12:09:00Z"),
            Daily("tradingagents","2026-11-05T11:59:00Z","2026-11-05T12:09:00Z"),"2026-11-05T12:00:00Z");
        Assert.Equal("UNAVAILABLE",result.GetProperty("Comparison").GetString());
        Assert.Equal("INVALID_VIBE",result.GetProperty("Reason").GetString());
    }
    [Fact]
    public void Rollover_requires_new_day_research_not_rejuvenated_previous_day()
    {
        var result=At(Daily(asOf:"2026-11-06T00:00:00Z",expires:"2026-11-07T00:00:00Z"),
            Daily("tradingagents","2026-11-06T00:00:00Z","2026-11-07T00:00:00Z"),"2026-11-06T00:00:00Z");
        Assert.Equal("AGREE_BUY",result.GetProperty("Comparison").GetString());
    }
}
