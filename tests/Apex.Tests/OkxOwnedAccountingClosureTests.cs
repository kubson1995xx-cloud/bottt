using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;

public sealed class OkxOwnedAccountingClosureTests
{
    private static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-11-04T09:00:00+00:00");
    private static readonly NavalInstrument Spec=new("ADA-USDC","ADA","USDC",.0001m,.0001m,.01m);
    private static readonly NavalStopOwnedReduction Cap=NavalStopOwnedReduction.Issue("parent","reservation",Guid.NewGuid());
    private sealed record Fixture(NavalBrokerEconomicPass Pass,NavalBrokerOperationalState State);
    private static Fixture Make(bool sell)
    {
        string client=sell?"ownSell":"ownBuy",order=sell?"778":"776";
        var f=new NavalBrokerRawFill("ADA-USDC",order,client,"900","901",sell?"sell":"buy",.0002m,109.99m,sell?-.000021998m:-.0000002m,sell?"USDC":"ADA","USDC",At,"fill");
        var fee=new NavalBrokerRawBill("901",f.Instrument,order,client,f.TradeId,"2",f.FeeCurrency!,f.SignedFee,sell?.021976002m:.0001998m,At,"anchor");
        var counter=new NavalBrokerRawBill("904",f.Instrument,order,client,f.TradeId,"2",sell?"ADA":"USDC",0,sell?-.0002m:-.021998m,At,"counter");
        var intent=new NavalBrokerIntent(sell?"owned-exit-parent":"parent","reservation",NavalExecutionOperation.Submit,f.Instrument,client,null,Array.Empty<byte>(),"request",At.AddSeconds(5),Guid.NewGuid(),1,1,0,1,NavalExecutionStage.Dispatching){DispatchRevision=2,Reduction=sell};
        var orderProof=new NavalBrokerOrderProof(f.Instrument,order,client,"filled",f.Quantity,f.Quantity,f.SignedFee,f.FeeCurrency,"USDC",null,null,"order");
        var pass=new NavalBrokerEconomicPass(At,1,true,new[]{f},new[]{fee,counter},new[]{orderProof},Array.Empty<NavalBrokerProtection>(),Array.Empty<NavalBrokerBalance>(),new Dictionary<string,NavalBrokerQuote>(),Array.Empty<NavalBrokerSurface>(),Array.Empty<string>());
        return new(pass,new NavalBrokerOperationalState{Intents=new[]{intent}});
    }
    private static HashSet<NavalBrokerRawBill> Paired(Fixture f)
    {
        var method=typeof(NavalPortfolioCoordinator).GetMethod("OwnedPairedTradeBills",BindingFlags.NonPublic|BindingFlags.Static);
        if(method!=null)return (HashSet<NavalBrokerRawBill>)method.Invoke(null,new object[]{f.Pass,f.State,Cap,Spec,"USDC"})!;
        // Original runtime has only the existing fee-bill helper. This preserves
        // the same records and executes that old classification, never a fake seal.
        var old=typeof(NavalPortfolioCoordinator).GetMethod("OwnedRecoveryBill",BindingFlags.NonPublic|BindingFlags.Static)!;
        return f.Pass.Bills.Where(b=>f.Pass.Fills.Any(v=>(bool)old.Invoke(null,new object[]{b,v})!)).ToHashSet();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Exact_full_fee_anchor_and_opposite_cash_leg_are_both_corroborated(bool sell)
    {
        var f=Make(sell);Assert.Equal(2,Paired(f).Count);
    }
    public static IEnumerable<object[]> InvalidPairs()=>new[]{"bill-id","client","empty-client","order","trade","instrument","currency","time","missing-time","fill-time","counter-fee","sign","anchor-sign","fee-component","missing-anchor","duplicate-id","conflicting-id","third-leg","duplicate-currency","foreign-anchor","missing-amount","fee-currency","actual-quote","fill-id","nonterminal","partial","order-fee","missing-own","undispatched","foreign-reservation","duplicate-fill","corrected-fill","retained-id-conflict","retained-fill-conflict","precision-product","precision-sum","overflow"}.SelectMany(v=>new[]{new object[]{false,v},new object[]{true,v}});
    [Theory][MemberData(nameof(InvalidPairs))]
    public void Any_unproved_or_conflicting_pair_is_not_paired_authority(bool sell,string fault)
    {
        var f=Make(sell);var p=f.Pass;var x=f.State;var a=p.Bills[0];var c=p.Bills[1];var fill=p.Fills[0];var o=p.Orders[0];
        switch(fault)
        {
            case "bill-id":c=c with{Id="901"};break;
            case "client":c=c with{ClientId="foreign"};break;
            case "empty-client":c=c with{ClientId=""};break;
            case "order":c=c with{OrderId="999"};break;
            case "trade":c=c with{TradeId="999"};break;
            case "instrument":c=c with{Instrument="BTC-USDC"};break;
            case "currency":c=c with{Currency="EUR"};break;
            case "time":c=c with{SourceUtc=At.AddMilliseconds(1)};break;
            case "missing-time":c=c with{SourceUtc=null};break;
            case "fill-time":fill=fill with{TradeUtc=null};break;
            case "counter-fee":c=c with{SignedFee=-.0001m};break;
            case "sign":c=c with{BalanceChange=-c.BalanceChange};break;
            case "anchor-sign":a=a with{BalanceChange=-a.BalanceChange};break;
            case "fee-component":a=a with{BalanceChange=fill.SignedFee};break;
            case "foreign-anchor":a=a with{ClientId="foreign"};break;
            case "missing-amount":c=c with{BalanceChange=null};break;
            case "fee-currency":fill=fill with{FeeCurrency="EUR"};break;
            case "actual-quote":fill=fill with{ActualQuote="EUR"};break;
            case "fill-id":fill=fill with{BillId="999"};break;
            case "nonterminal":o=o with{State="live"};break;
            case "partial":o=o with{Accumulated=.0001m};break;
            case "order-fee":o=o with{SignedFee=0};break;
            case "missing-own":x=x with{Intents=Array.Empty<NavalBrokerIntent>()};break;
            case "undispatched":x=x with{Intents=x.Intents.Select(i=>i with{DispatchRevision=null}).ToArray()};break;
            case "foreign-reservation":x=x with{Intents=x.Intents.Select(i=>i with{ReservationId="foreign"}).ToArray()};break;
            case "precision-product":fill=fill with{Quantity=.0000000000000000000000000001m,Price=1.1m};o=o with{Quantity=fill.Quantity,Accumulated=fill.Quantity};break;
            case "precision-sum":fill=fill with{Quantity=1m,Price=1m,SignedFee=-.0000000000000000000000000001m};a=a with{SignedFee=fill.SignedFee};o=o with{Quantity=1m,Accumulated=1m,SignedFee=fill.SignedFee};break;
            case "overflow":fill=fill with{Quantity=decimal.MaxValue,Price=2};o=o with{Quantity=fill.Quantity,Accumulated=fill.Quantity};break;
        }
        p=p with{Fills=new[]{fill},Orders=new[]{o},Bills=new[]{a,c}};
        if(fault=="missing-anchor")p=p with{Bills=new[]{c}};
        if(fault=="duplicate-id")p=p with{Bills=new[]{a,c,c}};
        if(fault=="conflicting-id")p=p with{Bills=new[]{a,c,c with{BalanceChange=c.BalanceChange-1,Digest="conflict"}}};
        if(fault=="third-leg")p=p with{Bills=new[]{a,c,c with{Id="905",Currency="EUR",Digest="third"}}};
        if(fault=="duplicate-currency")p=p with{Bills=new[]{a,c,c with{Id="905",Digest="duplicate"}}};
        if(fault=="duplicate-fill")p=p with{Fills=new[]{fill,fill}};
        if(fault=="corrected-fill")p=p with{Fills=new[]{fill,fill with{Price=fill.Price+1,Digest="correction"}}};
        if(fault=="retained-id-conflict")x=x with{Bills=new[]{c with{BalanceChange=c.BalanceChange-1,Digest="old-conflict"}}};
        if(fault=="retained-fill-conflict")x=x with{FillEvidence=new[]{fill with{SignedFee=0,Digest="old-fill-conflict"}}};
        Assert.Empty(Paired(new(p,x)));
    }
    [Theory]
    [InlineData("0.0000000000000000000000000001","1.1",true,false)]
    [InlineData("10","-0.0000000000000000000000000001",false,false)]
    [InlineData("1","-0.0000000000000000000000000001",false,true)]
    [InlineData("0.0002","109.99",true,true)]
    [InlineData("79228162514264337593543950335","2",true,false)]
    public void Paired_movement_arithmetic_never_rounds_an_unrepresentable_amount(string a,string b,bool multiply,bool expected)
    {
        var m=typeof(NavalPortfolioCoordinator).GetMethod("OwnedExactAmount",BindingFlags.NonPublic|BindingFlags.Static)!;
        var args=new object[]{decimal.Parse(a,System.Globalization.CultureInfo.InvariantCulture),decimal.Parse(b,System.Globalization.CultureInfo.InvariantCulture),multiply,0m};
        Assert.Equal(expected,(bool)m.Invoke(null,args)!);
    }
    [Theory]
    [InlineData("OWNED_EXACT_ORDER_UNKNOWN:parent",true)]
    [InlineData("OWNED_PROTECTION_TYPE_CONFIG_OR_IDENTITY_UNSUPPORTED_NO_SELL",true)]
    [InlineData("OWNED_FOREIGN_OR_UNLINKED_PENDING_ALGO_NO_SELL",true)]
    [InlineData("OWNED_EXACT_ORDER_UNKNOWN:foreign-parent",false)]
    [InlineData("OWNED_EXACT_ORDER_UNKNOWN:parent:other",false)]
    [InlineData("OWNED_PENDING_PROTECTION_CONFIGURATION_CONFLICT_NO_SELL:digest",false)]
    [InlineData("OWNED_PENDING_PROTECTION_CHILD_OR_RELATION_UNPROVEN_NO_SELL:digest",false)]
    [InlineData("OWNED_PROTECTION_TYPE_CONFIG_OR_IDENTITY_UNSUPPORTED_NO_SELL:digest",false)]
    [InlineData("OWNED_FOREIGN_OR_UNLINKED_PENDING_ALGO_NO_SELL:digest",false)]
    [InlineData("FOREIGN_FILL:digest",false)]
    [InlineData("UNLINKED_BILL:digest",false)]
    [InlineData("FILL_CORRECTION:digest",false)]
    public void Revalidation_allowlist_is_exact_and_parent_scoped(string marker,bool expected)
    {
        var m=typeof(NavalPortfolioCoordinator).GetMethod("OwnedObsoleteObservation",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.Equal(expected,(bool)m.Invoke(null,new object[]{marker,Cap})!);
    }
    [Theory]
    [InlineData("valid",true)][InlineData("canceled",true)][InlineData("full-net",true)]
    [InlineData("missing",false)][InlineData("duplicate",false)][InlineData("child",false)]
    [InlineData("foreign-client",false)][InlineData("foreign-parent",false)][InlineData("foreign-algo",false)]
    [InlineData("attachment",false)][InlineData("config",false)][InlineData("quantity",false)]
    [InlineData("nonterminal",false)][InlineData("allocation",false)][InlineData("retained-identity-conflict",false)]
    public void Final_status_revalidation_requires_exact_allocated_observed_protection(string fault,bool expected)
    {
        var f=Make(false);var o=f.Pass.Orders.Single() with{AttachmentClientId="ownOco",AlgoId="889",AttachmentId="888"};
        var r=new NavalReservation{Id="reservation",Quantity=.0002m,Intent=new("parent","signal","ADA-USDC",.0002m,109.99m,108m,112m)};
        var p=new NavalBrokerProtection("ADA-USDC",o.OrderId,"ownOco","889","live",.0001m,108m,112m,Array.Empty<string>(),"protection"){AttachmentId="888"};
        var ids=new[]{new NavalReservedClientId("parent","reservation",NavalClientIdRole.ProtectionAlgo,2,"ownOco")};
        var x=f.State with{IdAllocations=new[]{new NavalExecutionIdAllocation("parent","reservation",new[]{NavalClientIdRole.ProtectionAlgo},ids,"allocation")}};
        switch(fault)
        {
            case "canceled":p=p with{State="canceled"};break;
            case "full-net":p=p with{Quantity=.0001998m};break;
            case "child":p=p with{ChildOrderIds=new[]{"child"}};break;
            case "foreign-client":p=p with{AttachmentClientId="foreign"};break;
            case "foreign-parent":p=p with{ParentOrderId="999"};break;
            case "foreign-algo":p=p with{AlgoId="999"};break;
            case "attachment":p=p with{AttachmentId="999"};break;
            case "config":p=p with{Stop=107m};break;
            case "quantity":p=p with{Quantity=.00009m};break;
            case "nonterminal":o=o with{State="live"};break;
            case "allocation":x=x with{IdAllocations=Array.Empty<NavalExecutionIdAllocation>()};break;
            case "retained-identity-conflict":x=x with{Protection=new[]{p with{AlgoId="999"}}};break;
        }
        var pass=f.Pass with{Orders=new[]{o},Protection=fault=="missing"?Array.Empty<NavalBrokerProtection>():fault=="duplicate"?new[]{p,p}:new[]{p}};
        var m=typeof(NavalPortfolioCoordinator).GetMethod("OwnedObservedProtection",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.Equal(expected,(bool)m.Invoke(null,new object[]{pass,x,Cap,r,Spec})!);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Exact_replayed_rows_are_evidence_not_conflicting_replacements(bool sell)
    {
        var f=Make(sell);f=f with{State=f.State with{Bills=f.Pass.Bills,FillEvidence=f.Pass.Fills}};Assert.Equal(2,Paired(f).Count);
    }
}
