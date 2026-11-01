using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
namespace Apex.Core.Naval;
internal static class NavalBrokerSettlement
{
    internal static bool CanceledProtection(NavalBrokerIntent i,NavalBrokerProtection p)=>i.Operation==NavalExecutionOperation.CancelProtection&&p.Instrument==i.Instrument&&p.AlgoId==i.OrderId&&p.AttachmentClientId==i.ClientId&&p.State=="canceled"&&p.ChildOrderIds.Count==0;
    internal static bool FilledReduction(NavalBrokerIntent i,NavalBrokerOrderProof o,IEnumerable<NavalBrokerRawFill> fills,IEnumerable<NavalBrokerRawBill> bills)
    {
        if(!i.Reduction||i.Operation!=NavalExecutionOperation.Submit||o.Instrument!=i.Instrument||o.ClientId!=i.ClientId||i.Outcome?.OrderId!=null&&o.OrderId!=i.Outcome.OrderId||o.State!="filled"||o.Quantity is not >0||o.Accumulated!=o.Quantity)return false;
        using var doc=JsonDocument.Parse(i.Body);var p=doc.RootElement;
        if(o.Quantity!=decimal.Parse(p.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture)||o.ActualQuote!=p.GetProperty("tradeQuoteCcy").GetString()||o.FeeCurrency!=o.ActualQuote)return false;
        var rows=fills.Where(f=>f.Instrument==i.Instrument&&f.OrderId==o.OrderId&&f.ClientId==i.ClientId).GroupBy(f=>f.TradeId).Select(g=>g.Last()).ToArray();
        return rows.Length>0&&rows.All(f=>f.Side=="sell"&&f.Quantity is >0&&f.Price is >0&&f.SignedFee is <=0&&f.FeeCurrency==o.ActualQuote&&f.ActualQuote==o.ActualQuote&&bills.Any(b=>b.Type=="2"&&b.Instrument==f.Instrument&&b.OrderId==f.OrderId&&b.ClientId==f.ClientId&&b.TradeId==f.TradeId&&b.Currency==f.FeeCurrency&&b.SignedFee==f.SignedFee))&&rows.Sum(f=>f.Quantity)==o.Quantity&&rows.Sum(f=>f.SignedFee)==o.SignedFee;
    }
    internal static bool ZeroFillCanceled(NavalBrokerIntent i,NavalBrokerOrderProof o,IEnumerable<NavalBrokerRawFill> fills)
    {
        if(i.Operation!=NavalExecutionOperation.Submit||o.Instrument!=i.Instrument||o.ClientId!=i.ClientId||i.Outcome?.OrderId!=null&&o.OrderId!=i.Outcome.OrderId||o.State!="canceled"||o.Accumulated!=0||o.SignedFee!=0||fills.Any(f=>f.Instrument==i.Instrument&&(f.OrderId==o.OrderId||f.ClientId==i.ClientId)))return false;
        using var doc=JsonDocument.Parse(i.Body);var p=doc.RootElement;
        return p.GetProperty("ordType").GetString()=="fok"&&p.GetProperty("side").GetString()==(i.Reduction?"sell":"buy")&&o.Quantity==decimal.Parse(p.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture)&&o.ActualQuote==p.GetProperty("tradeQuoteCcy").GetString()&&o.FeeCurrency==o.ActualQuote;
    }
}
