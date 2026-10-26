using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Backtester.Data;
using Apex.Backtester.Research;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Model;
using Apex.Core.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace Apex.Backtester.Tests;

public class ResearchEvidenceTests : IDisposable
{
    readonly string dir = DataCliTests.CreateScratch(Environment.GetEnvironmentVariable("TMPDIR"), Path.GetTempPath);
    readonly ITestOutputHelper output;
    static readonly DateTime Origin = new(2023,1,1,0,0,0,DateTimeKind.Utc);
    public ResearchEvidenceTests(ITestOutputHelper output) { this.output=output; Directory.CreateDirectory(dir); }
    public void Dispose() => Directory.Delete(dir,true);
    static List<Bar> History()
    {
        var bars = new List<Bar>(); var rng = new Random(3); double price=4000;
        for(int day=0;day<314;day++)
        {
            double open=price;
            double close=(day>=297 && day<=299 || day>=310 && day<=312) ? open-45 : open+4+(rng.NextDouble()-0.5)*20;
            if(day==300) close=open+100;
            for(int hour=0;hour<24;hour++)
            {
                double o=open+(close-open)*hour/24, c=open+(close-open)*(hour+1)/24;
                if(day==311 && hour>=20 && hour<=22) o=c=open+20; // closed D1 bounce below SMA10 triggers a partial
                if(day==313) { o=open+(hour<12 ? hour : 24-hour)*2; c=open+(hour<11 ? hour+1 : 23-hour)*2; if(hour>=18) { o=open-(hour-18)*20; c=open-(hour-17)*20; } }
                bars.Add(new Bar(Origin.AddDays(day).AddHours(hour),o,Math.Max(o,c)+8,Math.Min(o,c)-8,c,1000));
            }
            price=close;
        }
        return bars;
    }
    DataLoader Loader(List<Bar> bars)
    {
        string csv=Path.Combine(dir,"US500_H1.csv");
        File.WriteAllLines(csv,new[] {"time,open,high,low,close,volume"}.Concat(bars.Select(b=>
            FormattableString.Invariant($"{b.OpenTime:yyyy-MM-dd HH:mm},{b.Open},{b.High},{b.Low},{b.Close},{b.Volume}"))));
        string manifest=Path.Combine(dir,"manifest.json");
        File.WriteAllText(manifest,JsonSerializer.Serialize(new {source="deterministic account-evidence fixture, not market data",timezone="UTC",files=new[] {new {
            file="US500_H1.csv",sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csv))).ToLowerInvariant(),quoteBasis="MID"}}}));
        return new DataLoader(dir,manifest,Origin,Origin.AddDays(300),Origin.AddDays(314),TimeSpan.FromHours(2));
    }
    [Fact] public void PartialThenLosingBoundaryExportsEveryFeeAndCashDeltaExactlyOnce()
    {
        var t=AccountingFxTests.T;
        var def=new InstrumentDef {Symbol="TEST",QuoteCcy="EUR",PipSize=1,LotSize=100,CommissionUsdPerLot=35,FinancingRate=0};
        var fx=new FxRates(); fx.Add("EURUSD",AccountingFxTests.Bars("EURUSD",(t.AddHours(-1),2),(t,2),(t.AddHours(1),2),(t.AddHours(2),2)));
        var world=new SimWorld(new[] {new SymbolData(def,AccountingFxTests.Bars("TEST",(t,100),(t.AddHours(1),110),(t.AddHours(2),90)),null)},fx,new CostScenario {SlippageMult=0},10000);
        world.SetTime(t); long id=AccountingFxTests.Open(world);
        world.SetTime(t.AddHours(1)); world.ClosePosition(id,4);
        world.SetTime(t.AddHours(3)); world.CloseAllAtEnd();
        Assert.Equal(9953,world.Balance,8);
        var evidence=JsonSerializer.SerializeToElement(world,DataManifest.JsonOptions);
        var flows=evidence.GetProperty("cashflows").EnumerateArray().ToArray();
        Assert.Equal(3,flows.Length);
        Assert.Equal(new[] {-3.5,78.6,-122.1},flows.Select(f=>Math.Round(f.GetProperty("delta").GetDouble(),8)));
        Assert.Equal(-7,flows.Sum(f=>f.GetProperty("commission").GetDouble()),8);
        Assert.Equal(-40,flows.Sum(f=>f.GetProperty("gross").GetDouble()),8);
        Assert.Equal(9953,flows.Last().GetProperty("balanceAfter").GetDouble(),8);
        Assert.Equal("BoundaryLiquidation",flows.Last().GetProperty("kind").GetString());
    }
    [Fact] public void ActualIsolatedWrapperExportsLosingBoundaryIncludingPriorPartialCashflows()
    {
        var bars=History(); var loader=Loader(bars);
        var entry=new ProfileEntry {AssetClass=AssetClass.Index,Strategy=StrategyId.IndexDipBuy,
            DipBuy=new IndexDipBuyParams {TrendSma=100, ExitSma=10, RsiEntry=15,StopAtr=20,
                Exit=new ExitPlan {PartialAtR=0.001,PartialFraction=0.25}}};
        var pipeline=new ResearchPipeline(loader,dir,null,null,Origin.AddDays(300),Origin.AddDays(314));
        var combo=new Combo {Strategy=StrategyId.IndexDipBuy, Index=new[] {9},Label="deterministic evidence fixture",Build=_=>entry};
        var run=pipeline.RunIsolated(entry,"US500",Origin.AddDays(300),Origin.AddDays(314),CostScenario.Base,combo,"selected-oos",2023);
        var actual=run.Account;
        output.WriteLine(string.Join("\n",actual.Trades.Select(t=>t.ToCsv())));
        output.WriteLine(JsonSerializer.Serialize(actual.Cashflows,DataManifest.JsonOptions));
        var winner=Assert.Single(run.NaturalTrades);
        Assert.True(winner.NetProfit>0);
        var boundary=Assert.Single(actual.Trades,t=>actual.BoundaryPositionIds.Contains(t.PositionId));
        Assert.True(boundary.NetProfit < -winner.NetProfit,"fixture must hide a larger boundary loss, not a profitable account");
        Assert.Contains(actual.Cashflows,f=>f.PositionId==boundary.PositionId && f.Kind=="Closed" && f.RemainingUnits>0);
        Assert.DoesNotContain(run.NaturalTrades,t=>t.PositionId==boundary.PositionId);
        Assert.Equal(actual.StartEquity+actual.Trades.Sum(t=>t.NetProfit),actual.EndEquity,8);
        Assert.Equal(actual.EndEquity,actual.StartEquity+actual.Cashflows.Sum(f=>f.Delta),8);
        foreach(var trade in actual.Trades)
        {
            var flows=actual.Cashflows.Where(f=>f.PositionId==trade.PositionId).ToArray();
            Assert.Equal(trade.NetProfit,flows.Sum(f=>f.Delta),8);
            Assert.Equal(trade.Commission,flows.Sum(f=>f.Commission),8);
            Assert.Equal(trade.Swap,flows.Sum(f=>f.Swap),8);
            Assert.Equal(trade.GrossProfit,flows.Sum(f=>f.Gross),8);
        }
        string path=Path.Combine(dir,"isolated","run-000001.json");
        Assert.True(File.Exists(path),"actual research wrapper must export complete account evidence, not just natural trades");
        using var exported=JsonDocument.Parse(File.ReadAllText(path));
        var root=exported.RootElement; var account=root.GetProperty("account");
        Assert.Equal("US500",root.GetProperty("symbol").GetString());
        Assert.Equal(combo.Key,root.GetProperty("comboKey").GetString());
        Assert.Equal("selected-oos",root.GetProperty("purpose").GetString());
        Assert.Equal(2023,root.GetProperty("foldYear").GetInt32());
        Assert.Equal(Origin.AddDays(300),root.GetProperty("from").GetDateTime());
        Assert.Equal(Origin.AddDays(314),root.GetProperty("to").GetDateTime());
        Assert.Equal(1,root.GetProperty("cost").GetProperty("commissionMult").GetDouble());
        Assert.Equal(actual.EndEquity,account.GetProperty("endEquity").GetDouble());
        Assert.Equal(actual.MaxEquityDrawdownPct,account.GetProperty("maxEquityDrawdownPct").GetDouble());
        Assert.Equal(2,account.GetProperty("trades").GetArrayLength());
        Assert.Equal(actual.Cashflows.Count,account.GetProperty("cashflows").GetArrayLength());
        var exportedFlows=account.GetProperty("cashflows").EnumerateArray().ToArray();
        Assert.Equal(account.GetProperty("net").GetDouble(),exportedFlows.Sum(f=>f.GetProperty("delta").GetDouble()),8);
        Assert.Equal(account.GetProperty("commission").GetDouble(),exportedFlows.Sum(f=>f.GetProperty("commission").GetDouble()),8);
        Assert.Equal(account.GetProperty("swap").GetDouble(),exportedFlows.Sum(f=>f.GetProperty("swap").GetDouble()),8);
        Assert.Equal(account.GetProperty("gross").GetDouble(),exportedFlows.Sum(f=>f.GetProperty("gross").GetDouble()),8);
        Assert.True(account.GetProperty("maxEquityDrawdownPct").GetDouble()>0);
        Assert.Equal(actual.EndEquity,account.GetProperty("dailyEquity").EnumerateArray().Last().GetProperty("equity").GetDouble(),8);
        Assert.Equal(boundary.PositionId,Assert.Single(account.GetProperty("boundaryPositionIds").EnumerateArray()).GetInt64());
        Assert.Equal(winner.NetProfit,root.GetProperty("naturalOutcomes").GetProperty("net").GetDouble(),8);
        Assert.Equal(actual.EndEquity-1000000,account.GetProperty("net").GetDouble(),8);
        var totals=JsonSerializer.SerializeToElement(IsolatedRun.AccountTotals(new[] {run}),DataManifest.JsonOptions);
        Assert.Equal(actual.EndEquity-1000000,totals.GetProperty("net").GetDouble(),8);
        Assert.Equal(1,totals.GetProperty("boundaryLiquidations").GetInt32());
        var wf=pipeline.ExportWalkForwardEvidence(StrategyId.IndexDipBuy,AssetClass.Index,new[] {run});
        var wfJson=JsonSerializer.SerializeToElement(wf,DataManifest.JsonOptions);
        Assert.Equal(winner.NetProfit,wfJson.GetProperty("naturalOos").GetProperty("net").GetDouble(),8);
        Assert.Equal(actual.EndEquity-1000000,wfJson.GetProperty("selectedOosAccounts").GetProperty("net").GetDouble(),8);
        var accountCsv=File.ReadAllLines(Path.Combine(dir,"trades","wf_account_IndexDipBuy_Index.csv"));
        var naturalCsv=File.ReadAllLines(Path.Combine(dir,"trades","wf_natural_IndexDipBuy_Index.csv"));
        Assert.Equal(3,accountCsv.Length); Assert.Equal(2,naturalCsv.Length);
        Assert.Contains(accountCsv,line=>line.StartsWith($"1;True;{boundary.PositionId};",StringComparison.Ordinal));
        Assert.DoesNotContain(naturalCsv,line=>line.StartsWith(boundary.PositionId+";",StringComparison.Ordinal));
        // Retain the real exported evidence when the external test command requests an evidence directory.
        if(Environment.GetEnvironmentVariable("APEX_TEST_EVIDENCE_DIR") is string evidence)
        {
            Directory.CreateDirectory(evidence);
            foreach(string file in new[] {path,Path.Combine(dir,"manifest.json"),Path.Combine(dir,"US500_H1.csv"),
                Path.Combine(dir,"trades","wf_account_IndexDipBuy_Index.csv"),Path.Combine(dir,"trades","wf_natural_IndexDipBuy_Index.csv")})
                File.Copy(file,Path.Combine(evidence,Path.GetFileName(file)),true);
        }
    }
}
