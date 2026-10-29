using System;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;
public class NavalCashDurabilityTests
{
    [Fact] public void NavalCash_RestartAndAmbiguousPersistence_RetainsPartialAndRequiresExplicitStart()
    {
        using var f=new NavalCashFixture();f.Run();var intent=f.Intent();var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(intent);
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,2,100,.2m)}));f.C.MarkUncertain(r.ReservationId!,"timeout");
        f.Restart();Assert.Equal("STOP",f.View.Mode);Assert.False(f.View.Ready);Assert.Equal(9799.8m,f.View.Cash);Assert.Equal(1,f.View.ActiveCount);
        Assert.Equal(NavalMutationStatus.Refused,f.Reserve("ETH").Status);
        Assert.Equal(NavalMutationStatus.Applied,f.Reconcile().Status);Assert.Equal("STOP",f.View.Mode);
        Assert.Equal(NavalMutationStatus.Applied,f.Start().Status);
        Assert.Equal(NavalMutationStatus.Refused,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(intent with{CommandId="fresh"}).Status);
        Assert.Equal(9799.8m,f.View.Cash);
    }
    [Theory] [InlineData("before",0)] [InlineData("after",1)] [InlineData("flush",1)]
    public void NavalCash_RestartAndAmbiguousPersistence_FreezeExactDurableOutcome(string fault,int expected)
    {
        using var f=new NavalCashFixture();f.Run();f.Store.Fault=fault;
        Assert.Equal(NavalMutationStatus.Stopped,f.Reserve().Status);var durable=f.Store.Value;
        f.Store.Fault=null;Assert.Equal(NavalMutationStatus.Stopped,f.Reserve("ETH").Status);Assert.Equal(durable,f.Store.Value);
        f.Restart();Assert.Equal(expected,f.View.ActiveCount);Assert.Equal("STOP",f.View.Mode);f.Run();
        Assert.Equal(expected,f.View.ActiveCount);
    }
    [Fact] public void NavalCash_TamperedIdentityAndLimits_ExclusiveOwnerAndChangedScopeRefuse()
    {
        using var f=new NavalCashFixture();f.Run();f.Reserve();
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));
        f.Owner.Dispose();var d=new NavalPortfolioDefinition(new("other","USD"),f.D.Limits,f.D.Members,f.D.Catalog,f.D.Fees);
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,d));
    }
    [Theory] [InlineData("missing")] [InlineData("corrupt")] [InlineData("schema")] [InlineData("fields")] [InlineData("CFD")] [InlineData("baseline")]
    public void NavalCash_RestartAndAmbiguousPersistence_CorruptionNeverReseeds(string fault)
    {
        using var f=new NavalCashFixture();f.Run();f.Reserve();f.Owner.Dispose();
        if(fault=="missing")f.Store.Value=null;
        if(fault=="corrupt")f.Store.Value="{";
        if(fault=="schema")f.Store.Value=f.Store.Value!.Replace("naval-cash-risk-v1","future-schema");
        if(fault=="fields")f.Store.Value=f.Store.Value!.Replace("\"EntryHold\":400.400","\"EntryHold\":0");
        if(fault=="baseline"){var e=JsonSerializer.Deserialize<EngineState>(f.Store.Value!)!;e.Naval=e.Naval! with{DayStartEquity=20000};f.Store.Value=JsonSerializer.Serialize(e);}
        if(fault=="CFD"){var e=JsonSerializer.Deserialize<EngineState>(f.Store.Value!)!;e.AccountNumber=123;f.Store.Value=JsonSerializer.Serialize(e);}
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));
    }
    [Fact] public void NavalCash_TamperedIdentityAndLimits_ChangedConfigAndCatalogNeverLoads()
    {
        using var f=new NavalCashFixture();f.Run();f.Reserve();f.Owner.Dispose();
        foreach(var d in new[]{new NavalPortfolioDefinition(f.D.Id,new NavalCashLimits(TradePct:.4m),f.D.Members,f.D.Catalog,f.D.Fees),
            new NavalPortfolioDefinition(f.D.Id,f.D.Limits,f.D.Members,f.D.Catalog.Select(i=>i with{Quantum=.01m}),f.D.Fees)})
            Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,d));
    }
    [Fact] public void NavalCash_RestartAndAmbiguousPersistence_OldPermitAndReceiptCannotStart()
    {
        using var f=new NavalCashFixture();f.Reconcile();var s=f.View;var permit=new NavalStartPermit(f.C.OwnerToken,s.DurableRevision,f.Generation,true);
        f.Reconcile();Assert.Equal(NavalMutationStatus.Refused,f.C.Start(permit).Status);
        var e=f.Receipt(NavalEvidenceKind.Reconcile,complete:true);f.Restart();
        Assert.Equal(NavalMutationStatus.Refused,f.C.Reconcile(e).Status);Assert.Equal(NavalMutationStatus.Refused,f.C.Start(permit).Status);
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize<NavalStartPermit>("{}"));Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize<NavalEvidenceReceipt>("{}"));
    }
    [Fact] public void NavalCash_UnchangedCfd_NullNavalRoundTripPreservesMarginDefaults()
    {
        var s=JsonSerializer.Deserialize<EngineState>("{\"SchemaVersion\":5,\"AccountNumber\":123,\"RealizedTotal\":42}")!;
        Assert.Null(s.Naval);Assert.Equal(123,s.AccountNumber);Assert.Equal(42,s.RealizedTotal);
        Assert.Null(JsonSerializer.Deserialize<EngineState>(JsonSerializer.Serialize(s))!.Naval);
        Assert.Equal(5,new EngineConfig().Risk.MaxEffectiveLeverage);
    }
}
