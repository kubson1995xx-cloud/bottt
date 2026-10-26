using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Apex.Core.Bridge;

namespace Apex.Platforms.CTrader;

/// <summary>CLR-only event fence. SDK enumeration/Observe/Capture run on the SDK main thread.
/// Events change epoch immediately even if their main-thread notification is delayed. A lock
/// serializes the final epoch check and SDK invocation against concurrent disconnect callbacks.</summary>
public sealed class ScopedPlatformState
{
    private readonly object gate = new();
    private readonly string startup = Guid.NewGuid().ToString("N");
    private long epoch, positionsRevision, pendingRevision;
    private bool connected;
    private string positions = "", pending = "";
    private readonly Dictionary<string, (ScopeFrame frame, long tick)> captures = new();
    public string BrokerEpoch { get { lock (gate) return startup + ":" + epoch; } }
    public bool BrokerConnected { get { lock (gate) return connected; } }
    public void BrokerEvent(bool value)
    { lock (gate) { epoch = checked(epoch + 1); connected = value; captures.Clear(); } }
    public void Observe(IEnumerable<PositionDto> currentPositions, IEnumerable<PendingOrderDto> currentOrders)
    {
        // Deliberately exclude changing mark-to-market PnL/price; include entry, ownership,
        // membership, ALL volume and protection fields. Pending economics are never truncated.
        var p = BridgeProtocol.Serialize(currentPositions.OrderBy(x => x.Id).Select(x => new { x.Id, x.Symbol, x.Side, x.Units, x.Entry, x.EntryTime, x.Sl, x.Tp, x.Label, x.Comment }).ToArray());
        var o = BridgeProtocol.Serialize(currentOrders.OrderBy(x => x.Id).ToArray());
        lock (gate)
        {
            if (p != positions) { positionsRevision = checked(positionsRevision + 1); positions = p; }
            if (o != pending) { pendingRevision = checked(pendingRevision + 1); pending = o; }
        }
    }
    public ScopeFrame Current(Guid baseline, string label, string transport) => new()
    { BaselineId = baseline, Label = label, TransportGeneration = transport, BrokerEpoch = BrokerEpoch,
        BrokerConnected = BrokerConnected, PositionRevision = positionsRevision, PendingOrderRevision = pendingRevision };
    public ScopeFrame Capture(Guid baseline, string label, string transport)
    {
        lock (gate)
        {
            var frame = Current(baseline, label, transport) with { SnapshotId = Guid.NewGuid().ToString("N") };
            foreach (var id in captures.Where(x => Elapsed(x.Value.tick) >= 30).Select(x => x.Key).ToArray()) captures.Remove(id);
            if (captures.Count >= 32) throw new InvalidOperationException("scoped captures full");
            captures.Add(frame.SnapshotId, (frame, Stopwatch.GetTimestamp())); return frame;
        }
    }
    private static double Elapsed(long tick) => (double)(Stopwatch.GetTimestamp() - tick) / Stopwatch.Frequency;
    public bool CanOpen(ScopeFrame? frame)
    {
        lock (gate) return frame != null && frame.BaselineId != Guid.Empty && frame.Label.Length is > 0 and <= 20 && frame.TransportGeneration.Length > 0
            && frame.ExecutionPath == "desktop-bridge" && frame.SafetyVersion == "scoped-demo-v1"
            && connected && frame.BrokerConnected == true && frame.BrokerEpoch == BrokerEpoch
            && frame.PositionRevision == positionsRevision && frame.PendingOrderRevision == pendingRevision
            && captures.TryGetValue(frame.SnapshotId, out var source) && source.frame == frame && Elapsed(source.tick) < 30;
    }
    public bool TryRun(ScopeFrame? frame, Func<bool> safe, Action execute)
    {
        lock (gate)
        {
            if (!CanOpen(frame) || !safe()) return false;
            // Observe in safe() may have changed revisions; recheck after it, immediately at SDK boundary.
            if (!CanOpen(frame)) return false;
            execute(); return true;
        }
    }
}
