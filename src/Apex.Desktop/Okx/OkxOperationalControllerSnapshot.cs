using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Core.Engine;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

internal sealed partial class OkxOperationalTradingService
{
    internal OkxControllerSnapshot ReadControllerSnapshot()
    {
        var now=clock.GetUtcNow();
        if(!SourceCurrent)return OkxControllerSnapshotCodec.Disconnected(now);
        var state=session.ReadState();var status=ReadStatus();var view=session.Coordinator.Snapshot();
        // No financial lock is held while formatting/writing output. A concurrent commit
        // refuses this poll instead of combining different owner revisions or retrying.
        if(state.DurableRevision!=status.Revision||state.DurableRevision!=session.ReadState().DurableRevision||!SourceCurrent)
            throw new InvalidDataException("CONTROLLER_SNAPSHOT_CHANGED");
        return OkxControllerSnapshotProjector.Project(state,status,view,now);
    }
}
internal static class OkxControllerSnapshotProjector
{
    internal static OkxControllerSnapshot Project(EngineState state,OkxOperationalTradingStatus status,NavalPortfolioView view,DateTimeOffset now)
    {
        if(!status.Connected)return OkxControllerSnapshotCodec.Disconnected(now);
        var b=state.NavalBroker??throw new InvalidDataException("CONTROLLER_OWNER_REQUIRED");var s=state.Naval??throw new InvalidDataException("CONTROLLER_OWNER_REQUIRED");var x=b.Operational;
        var baseline=b.Baseline.Observations;
        bool operational=x?.LastBalances!=null;
        var balances=operational?x!.LastBalances!:baseline.Balances;
        var capture=operational?x!.LastBalanceCaptureUtc:baseline.CaptureUtc;
        var evidence=operational?x!.Evidence:baseline.Surfaces;
        var balanceSource=evidence.LastOrDefault(e=>e.Endpoint=="/api/v5/account/balance");
        var excluded=baseline.FiniteAllocation?.Excluded.Select(v=>v.Currency).ToHashSet(StringComparer.Ordinal)??new HashSet<string>(StringComparer.Ordinal);
        var catalog=NavalOrdinaryCatalogPolicy.Catalog(b);
        // Index retained proof once; polling must not multiply every order, intent,
        // allocation and protective child while the command consumer waits.
        var intents=(x?.Intents??Array.Empty<NavalBrokerIntent>()).Where(i=>i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null).ToLookup(i=>(i.Instrument,i.ClientId));
        var allocations=(x?.IdAllocations??Array.Empty<NavalExecutionIdAllocation>()).SelectMany(a=>a.Ids).Where(i=>i.Role==NavalClientIdRole.ProtectionAlgo).Select(i=>(i.ReservationId,i.ClientId)).ToHashSet();
        var orders=x?.Orders??Array.Empty<NavalBrokerOrderProof>();
        var parents=orders.SelectMany(o=>intents[(o.Instrument,o.ClientId)].Where(i=>i.Outcome?.OrderId==null||i.Outcome.OrderId==o.OrderId).Select(i=>(Order:o,Intent:i))).ToArray();
        var parentIndex=parents.ToLookup(p=>(p.Order.Instrument,p.Order.OrderId));
        var orderRevisions=parents.GroupBy(p=>(p.Order.Instrument,p.Order.OrderId)).ToDictionary(g=>g.Key,g=>g.Max(p=>p.Intent.PreparedRevision));
        var ownProtection=(x?.Protection??Array.Empty<NavalBrokerProtection>()).Where(p=>parentIndex[(p.Instrument,p.ParentOrderId)].Any(parent=>!parent.Intent.Reduction&&allocations.Contains((parent.Intent.ReservationId,p.AttachmentClientId)))).ToArray();
        long ProtectionRevision(NavalBrokerProtection p)=>parentIndex[(p.Instrument,p.ParentOrderId)].Max(parent=>parent.Intent.PreparedRevision);
        foreach(var p in ownProtection)foreach(string child in p.ChildOrderIds)
            orderRevisions[(p.Instrument,child)]=Math.Max(orderRevisions.GetValueOrDefault((p.Instrument,child)),ProtectionRevision(p));
        var protectedReservations=ownProtection.Where(p=>p.State=="live").SelectMany(p=>parentIndex[(p.Instrument,p.ParentOrderId)].Where(parent=>allocations.Contains((parent.Intent.ReservationId,p.AttachmentClientId))).Select(parent=>(parent.Intent.ReservationId,p.Quantity))).ToLookup(p=>p.ReservationId,p=>p.Quantity);
        var reservationRevisions=(x?.Intents??Array.Empty<NavalBrokerIntent>()).GroupBy(i=>i.ReservationId).ToDictionary(g=>g.Key,g=>g.Max(i=>i.PreparedRevision));
        string Protection(NavalReservation r)=>r.QuarantineMark!=null?"UNPROTECTED_QUARANTINED_FULL_LOSS_RESERVED":r.Inventory==0?"NO_INVENTORY":
            x?.Obligations.Contains("PROTECTION_GAP:"+r.Id)==true?"PROTECTION_GAP":protectedReservations[r.Id].Any(quantity=>quantity>=r.Inventory)?"OBSERVED_PROTECTION":"PROTECTION_GAP";
        bool historyAvailable=s.Fills.Count>0||x?.Orders.Count>0||x?.Protection.Count>0||x?.Evidence.Any(e=>e.Endpoint is "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive" or "/api/v5/trade/fills" or "/api/v5/trade/fills-history")==true;
        return new(OkxControllerSnapshotCodec.Schema,now,true,view.Mode,status.Armed,status.Ready,view.EmergencyLatch,state.DurableRevision,
            new(b.Baseline.Binding.Account.QuoteCurrency,view.Cash,view.Held,view.Free,s.Reservations.Sum(r=>r.AllocatedCost),view.LiquidationEquity),
            new(operational?"OPERATIONAL":"BASELINE",capture,balanceSource?.RequestUtc,balanceSource?.ReceiveUtc,s.ReconciledUtc,evidence.Count>0,historyAvailable),status.Evaluation,
            Window(balances.OrderBy(v=>v.Currency,StringComparer.Ordinal).Select(v=>new OkxControllerBalance(v.Currency,v.Cash,v.Available,v.Frozen,v.SourceUtc,excluded.Contains(v.Currency))),OkxControllerSnapshotCodec.MaxBalances),
            Window(catalog.OrderBy(i=>i.Instrument,StringComparer.Ordinal).Select(i=>new OkxControllerInstrument(i.Instrument,i.BaseCurrency,i.QuoteCurrency,i.Quantum,i.MinimumQuantity,i.TickSize)),OkxControllerSnapshotCodec.MaxCatalog),
            Window(s.Reservations.Where(r=>r.Inventory>0||r.Pending>0).OrderByDescending(r=>reservationRevisions.GetValueOrDefault(r.Id)).ThenBy(r=>r.Id,StringComparer.Ordinal).Select(r=>new OkxControllerInventory(r.Intent.Instrument,r.Inventory,r.Pending,r.AllocatedCost,r.RiskHold,r.ReductionUnsettled,Protection(r)))),
            Window(orders.Where(o=>orderRevisions.ContainsKey((o.Instrument,o.OrderId))).OrderByDescending(o=>orderRevisions[(o.Instrument,o.OrderId)]).ThenByDescending(o=>o.OrderId,StringComparer.Ordinal).ThenBy(o=>o.Instrument,StringComparer.Ordinal).Select(o=>new OkxControllerOrder(o.Instrument,o.State,o.Quantity,o.Accumulated,o.SignedFee,string.IsNullOrEmpty(o.FeeCurrency)?null:o.FeeCurrency))),
            Window(ownProtection.OrderByDescending(ProtectionRevision).ThenByDescending(p=>p.AlgoId,StringComparer.Ordinal).ThenBy(p=>p.Instrument,StringComparer.Ordinal).Select(p=>new OkxControllerProtection(p.Instrument,p.State,p.Quantity,p.Stop,p.TakeProfit))),
            Window(s.Fills.OrderByDescending(f=>f.Fill.TradeUtc).ThenByDescending(f=>f.Fill.TradeId,StringComparer.Ordinal).ThenBy(f=>f.Fill.Instrument,StringComparer.Ordinal).Select(f=>new OkxControllerFill(f.Fill.Instrument,f.Fill.Reduction,f.Fill.Quantity,f.Fill.Price,f.Fill.Fee,f.Fill.FeeCurrency,f.Fill.TradeUtc))));
    }
    private static OkxControllerWindow<T> Window<T>(IEnumerable<T> source,int cap=OkxControllerSnapshotCodec.Window)
    {var all=source.ToArray();return new(all.Length,all.Length>cap,all.Take(cap).ToArray());}
}
