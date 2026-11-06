using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

// Explicit synthetic report operands, never real inference or runtime enrollment.
public sealed class OkxWindowsAdvisoryConsumerTests
{
    internal static readonly DateTimeOffset Cutoff=DateTimeOffset.Parse("2026-11-06T00:00:00Z");
    internal static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal sealed class Reports
    {
        internal Dictionary<string,byte[]> Files=new();
        internal string Digest=new('a',64),Generation=new('b',32);
        internal Action<string,JsonObject>? Mutate;
        internal void Build()
        {
            Files.Clear();var tools=new JsonObject();
            foreach(string tool in new[]{"vibe-trading","tradingagents"})
            {
                string original=tool=="vibe-trading"?"BUY":"Overweight";
                var native=new JsonObject { ["native"]=new JsonObject{["native_rating"]=original,["recommendation"]="BUY",["summary"]="Explicit synthetic test operand",["module"]="synthetic/source.py",["orchestration_module"]="synthetic/graph.py",["DatasetSha256"]=Digest,["tool_calls"]=2},["error"]=null };
                var report=new JsonObject{["Schema"]="apex-advisory-v1",["Status"]="ok",["Tool"]=tool,["Instrument"]="BTC-EUR",["QuoteCurrency"]="EUR",["AsOfUtc"]="2026-11-06T00:00:00Z",["ExpiresUtc"]="2026-11-07T00:00:00Z",["DatasetSha256"]=Digest,["Provider"]="openai-codex",["Model"]="gpt-6.1-sol",["Recommendation"]="BUY",["ExecutionAllowed"]=false,["EvidenceSources"]=new JsonArray("https://eea.okx.com/api/v5/market/candles"),["Summary"]="Synthetic test only",["SourceCommit"]=tool=="vibe-trading"?"8172827ec537146920e112a9952e8e7e063e218b":"9fa1196fffbcfd9de5146d2dd2869df84ab99838"};
                Mutate?.Invoke(tool+".json",report);Mutate?.Invoke(tool+"-native.json",native);
                string folder="generations/"+Generation+"/";
                Files[folder+tool+".json"]=JsonSerializer.SerializeToUtf8Bytes(report);Files[folder+tool+"-native.json"]=JsonSerializer.SerializeToUtf8Bytes(native);
                tools[tool]=new JsonObject{["ReportSha256"]=Hash(Files[folder+tool+".json"]),["NativeSha256"]=Hash(Files[folder+tool+"-native.json"]),["OriginalRecommendation"]=original,["MappedRecommendation"]="BUY",["State"]="completed"};
            }
            var manifest=new JsonObject{["Schema"]="apex-advisory-pair-v1",["Generation"]=Generation,["Instrument"]="BTC-EUR",["DatasetSha256"]=Digest,["AsOfUtc"]="2026-11-06T00:00:00Z",["ExpiresUtc"]="2026-11-07T00:00:00Z",["Inference"]="genuine",["Tools"]=tools};
            Mutate?.Invoke("manifest.json",manifest);
            Files["generations/"+Generation+"/manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(manifest);
            var pointer=new JsonObject{["Schema"]="apex-advisory-pointer-v1",["Generation"]=Generation,["ManifestSha256"]=Hash(Files["generations/"+Generation+"/manifest.json"])};
            Mutate?.Invoke("CURRENT.json",pointer);Files["CURRENT.json"]=JsonSerializer.SerializeToUtf8Bytes(pointer);
        }
        internal bool Admits(DateTimeOffset? now=null,string? digest=null)
        {
            var type=typeof(OkxAdvisoryComparison).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisorySnapshot");
            Assert.NotNull(type); // Behavioral RED on missing production consumer, no compile-error substitute.
            var parse=type!.GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic);Assert.NotNull(parse);
            try
            {
                var snapshot=parse!.Invoke(null,new object[]{(Func<string,byte[]>)(p=>Files[p]),"BTC-EUR",digest??Digest,Cutoff,now??Cutoff.AddHours(12)});
                Assert.NotNull(snapshot);return (bool)type.GetProperty("AllowsEntry",BindingFlags.Instance|BindingFlags.Public)!.GetValue(snapshot)!;
            }
            catch(TargetInvocationException e) when(e.InnerException is InvalidDataException or JsonException or KeyNotFoundException){return false;}
        }
    }
    [Fact] public void Current_hash_bound_genuine_labelled_BUY_pair_is_read_by_actual_consumer()
    {var r=new Reports();r.Build();Assert.True(r.Admits());}
    [Theory]
    [InlineData("Inference","fixture")][InlineData("Instrument","SOL-EUR")][InlineData("DatasetSha256","wrong")][InlineData("Schema","unknown")]
    public void Closed_generation_identity_refuses(string key,string value)
    {var r=new Reports{Mutate=(p,n)=>{if(p=="manifest.json")n[key]=value;}};r.Build();Assert.False(r.Admits());}
    [Theory]
    [InlineData("Model","other")][InlineData("Provider","other")][InlineData("SourceCommit","wrong")][InlineData("QuoteCurrency","USDT")][InlineData("Status","unavailable")][InlineData("Recommendation","WAIT")][InlineData("Recommendation","SELL")][InlineData("extra","unknown")]
    public void Rehashed_invalid_report_cannot_admit(string key,string value)
    {var r=new Reports{Mutate=(p,n)=>{if(p=="vibe-trading.json")n[key]=value;}};r.Build();Assert.False(r.Admits());}
    [Fact] public void Expiry_wrong_actual_data_and_tampered_native_refuse()
    {var r=new Reports();r.Build();Assert.False(r.Admits(Cutoff.AddDays(1)));Assert.False(r.Admits(digest:new string('c',64)));r.Files["generations/"+r.Generation+"/tradingagents-native.json"]=new byte[]{123,125};Assert.False(r.Admits());}
    [Fact]
    public void Production_daily_series_is_the_same_pinned_strategy_and_supervisor_input_not_rebased()
    {
        var type=typeof(OkxAdvisoryComparison).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisoryCoordinator")!;
        var pin=type.GetMethod("PinSeries",BindingFlags.Instance|BindingFlags.NonPublic);Assert.NotNull(pin);
        var coordinator=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{"unused"},null)!;
        var bars=Enumerable.Range(0,299).Select(i=>new Apex.Core.Naval.NavalBar(Cutoff.AddDays(i-299),100,102,99,101,1,true)).ToArray();
        var first=new Apex.Core.Naval.NavalSeries("BTC-EUR","BTC","EUR","OKX EEA public /market/candles 1Dutc","trade",bars);
        var frozen=(Apex.Core.Naval.NavalSeries)pin!.Invoke(coordinator,new object[]{first,Cutoff})!;
        bars[0]=bars[0] with{Close=100};
        var rolling=first with{Bars=bars.Skip(1).ToArray()};
        var again=pin.Invoke(coordinator,new object[]{rolling,Cutoff});Assert.Same(frozen,again);Assert.Equal(299,frozen.Bars.Count);Assert.Equal(101,frozen.Bars[0].Close);
    }
    [Fact] public void Traversal_generation_refuses_before_reader_path_access()
    {var r=new Reports{Mutate=(p,n)=>{if(p=="CURRENT.json")n["Generation"]="../../outside";}};r.Build();Assert.False(r.Admits());}
}
