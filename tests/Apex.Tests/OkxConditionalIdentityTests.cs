using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxConditionalTradingTests
{
    // Same first position, signed request, size, fees and exact forwarded client as
    // the retained independent probe. Only the generated numeric ID varies.
    [Theory]
    [InlineData("algoId","888")]
    [InlineData("algoClOrdId","Foreign1")]
    public async Task Protective_child_must_not_credit_foreign_generated_identity(string field,string value)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        TradingEconomicsFixture.InstallTriggeredExit(f,peer);var basis=f.Mutate!;f.Mutate=(path,body)=>
        {
            basis(path,body);
            if(path is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")
                foreach(var row in body["data"]!.AsArray())if(row!["ordId"]?.GetValue<string>()=="778")row[field]=value;
        };
        await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Ready);
        Assert.Equal(peer.Size,State(f).Naval!.Reservations.Single().Inventory);Assert.DoesNotContain(State(f).Naval!.Fills,p=>p.Fill.Reduction);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Previously_bound_generated_numeric_identity_cannot_rebind_on_same_owned_client()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);LivePending(f,peer,"889");Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        var basis=f.Mutate!;f.Mutate=(path,body)=>{basis(path,body);if(path is "/api/v5/trade/order-algo" or "/api/v5/trade/orders-algo-pending")foreach(var row in body["data"]!.AsArray())row!["algoId"]="890";};
        await loop.ReconcileAsync(default);Assert.False(loop.ReadStatus().Ready);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.DoesNotContain(State(f).NavalBroker!.Operational!.Protection,p=>p.AlgoId=="890");Assert.Equal(1,peer.Posts);
    }
    private static void LivePending(OperationalPeer f,TradingPeer peer,string generated)
    {
        var basis=f.Mutate!;
        f.Mutate=(path,body)=>
        {
            basis(path,body);
            if(path=="/api/v5/trade/order-algo")
            {
                if(f.Paths.Last().Contains("algoId=888",StringComparison.Ordinal)&&generated!="888")body["data"]=new JsonArray();
                else {body["data"]![0]!["algoId"]=generated;if(peer.ProtectionCanceled)body["data"]![0]!["state"]="canceled";}
            }
            if(path=="/api/v5/trade/orders-algo-pending"&&f.Paths.Last().Contains("ordType=conditional,oco",StringComparison.Ordinal)&&!peer.ProtectionCanceled)
            {basis("/api/v5/trade/order-algo",body);body["data"]![0]!["algoId"]=generated;}
        };
    }
    [Theory]
    [InlineData("888")]
    [InlineData("889")]
    public async Task Attachment_and_generated_numeric_identity_survive_first_position_restart_and_exact_recovery(string generated)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);
        peer.GeneratedAlgoId=generated;await using var loop=Production(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);Assert.Equal(1,peer.Posts);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);LivePending(f,peer,generated);
        var reconciled=await loop.ReconcileAsync(default);Assert.True(reconciled.Status==NavalMutationStatus.Applied,reconciled.Code);
        Assert.Equal("RUNNING",loop.ReadStatus().Mode);Assert.True(loop.ReadStatus().Ready);Assert.Empty(loop.ReadStatus().Obligations);
        var state=State(f);var parent=state.NavalBroker!.Operational!.Orders.Last(o=>o.OrderId=="777");
        using var proof=JsonDocument.Parse(JsonSerializer.Serialize(parent));
        Assert.Equal("888",proof.RootElement.GetProperty("AttachmentId").GetString());Assert.Equal(generated,parent.AlgoId);
        var protection=state.NavalBroker.Operational.Protection.Last();Assert.Equal(generated,protection.AlgoId);Assert.Equal("777",protection.ParentOrderId);Assert.Equal(peer.Attachment,protection.AttachmentClientId);
        Assert.All(f.Paths.Where(p=>p.StartsWith("/api/v5/trade/order-algo?",StringComparison.Ordinal)),p=>{Assert.Contains("algoClOrdId="+peer.Attachment,p);Assert.DoesNotContain("&algoId=",p);});
        var exit=await loop.ExitAsync(state.Naval!.Reservations.Single().Id,default);Assert.True(exit.Status==NavalMutationStatus.Applied,exit.Code);Assert.Equal(3,peer.Posts);
        var canceled=State(f).NavalBroker!.Operational!;Assert.Contains(canceled.Intents,i=>i.Operation==NavalExecutionOperation.CancelProtection&&i.OrderId==generated&&i.ClientId==peer.Attachment);
        Assert.Contains(canceled.Protection,p=>p.AlgoId==generated&&p.State=="canceled");
        await loop.DisposeAsync();await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
        Assert.Equal(generated,State(f).NavalBroker!.Operational!.Protection.Last().AlgoId);Assert.Equal(peer.Size,State(f).Naval!.Reservations.Single().Inventory);Assert.Equal(3,peer.Posts);
    }
    [Theory]
    [InlineData("detail-client")]
    [InlineData("pending-client")]
    [InlineData("parent-client")]
    public async Task Distinct_identity_never_owns_foreign_client_or_parent(string fault)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);LivePending(f,peer,"889");var basis=f.Mutate!;
        f.Mutate=(path,body)=>
        {
            basis(path,body);
            if((fault=="detail-client"&&path=="/api/v5/trade/order-algo"||fault=="pending-client"&&path=="/api/v5/trade/orders-algo-pending")&&body["data"]!.AsArray().Count>0)body["data"]![0]!["algoClOrdId"]="Foreign1";
            if(fault=="parent-client"&&path is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")foreach(var row in body["data"]!.AsArray())row!["attachAlgoOrds"]![0]!["attachAlgoClOrdId"]="Foreign1";
        };
        await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Ready);Assert.NotEmpty(loop.ReadStatus().Obligations);
        await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.Equal(fault=="pending-client"?2:1,peer.Posts);Assert.Empty(peer.ExitClient);
    }
}
