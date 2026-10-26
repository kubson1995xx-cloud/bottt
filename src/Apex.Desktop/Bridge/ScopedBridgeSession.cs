using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Apex.Core.Bridge;
using Apex.Core.Engine;

namespace Apex.Desktop.Bridge;

internal sealed record ScopedSnapshot(ScopedReceipt Receipt, ScopeFrame Source, IReadOnlyList<ResultMsg> Recovered, TimeSpan ReceivedMonotonic);

public sealed partial class BridgeSession
{
    internal static TimeSpan MonotonicNow() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    public bool ScopedCapable { get; private set; }
    public string PeerLabel { get; private set; } = "";
    internal IReadOnlyList<PlatformStateInventory>? DeploymentInventory { get; private set; }
    public string BrokerEpoch { get; private set; } = "";
    public bool BrokerConnected { get; private set; }
    private Guid _baselineId;
    private long _pendingRevision;
    private long _requestPendingRevision;
    private string _requestBrokerEpoch = "";
    private ScopeFrame? _liveScope;
    private List<PendingOrderDto> _pendingOrders = new();
    private DateTime _pendingReceivedUtc;
    private DateTime _historyCaptureRequestedUtc;
    internal bool ProtocolFault { get; private set; }
    private void FaultProtocol()
    { lock (_lock) { _historyFailed = true; _history = new(); _acceptedScope = null; if (ScopedCapable) ProtocolFault = true; } }
    internal bool SourceInventoryReady
    {
        get { lock (_lock) return HasFreshSnapshots && !ProtocolFault && BrokerConnected && DateTime.UtcNow - _pendingReceivedUtc < TimeSpan.FromSeconds(30)
            && _pendingReceivedTick is { } pt && MonotonicNow() - pt < TimeSpan.FromSeconds(30)
            && _liveScope is { } f && f.BrokerEpoch == BrokerEpoch && f.BrokerConnected == true && f.Label == PeerLabel
            && f.TransportGeneration == Id && f.BaselineId == _baselineId && f.ExecutionPath == "desktop-bridge"
            && f.SafetyVersion == ScopedRecoveryPolicy.SafetyVersion && f.PositionRevision is >= 0 && f.PendingOrderRevision is >= 0; }
    }
    private ScopedSnapshot? _acceptedScope;
    internal event Action<BridgeSession, ScopedSnapshot>? ScopedCompleted;
    internal event Action<BridgeSession>? BrokerChanged;
    internal Guid BaselineId { get { lock (_lock) return _baselineId; } }
    internal string[] RecoverOperations { get; set; } = Array.Empty<string>();
    internal ResultMsg? LookupRecovered(string actionId)
    { lock (_lock) return _acceptedScope?.Recovered.SingleOrDefault(r => r.Id == actionId); }
    public IReadOnlyList<PendingOrderDto> PendingOrders { get { lock (_lock) return _pendingOrders.ToArray(); } }
    internal void ConfigureScope(Guid id) { lock (_lock) { _baselineId = id; _acceptedScope = null; } }
    private void ReceiveBroker(BrokerStateMsg state)
    {
        bool changed;
        lock (_lock)
        {
            changed = state.BrokerEpoch != BrokerEpoch || state.BrokerConnected != BrokerConnected;
            if (changed)
            {
                BrokerEpoch = state.BrokerEpoch;
                BrokerConnected = state.BrokerConnected == true && !string.IsNullOrWhiteSpace(BrokerEpoch);
                _acceptedScope = null; _history = new(); _historyFailed = true; _historyStarted = DateTime.MinValue;
            }
        }
        if (changed) BrokerChanged?.Invoke(this);
    }
    private bool ValidScope(ScopeFrame? scope) => scope != null && scope.BaselineId == _baselineId && _baselineId != Guid.Empty
        && scope.Label == _historyLabel && scope.Label == PeerLabel && scope.ExecutionPath == "desktop-bridge"
        && scope.SafetyVersion == ScopedRecoveryPolicy.SafetyVersion && scope.TransportGeneration == Id
        && scope.BrokerEpoch == BrokerEpoch && BrokerConnected && scope.BrokerConnected == true
        && scope.PositionRevision is >= 0 && scope.PendingOrderRevision is >= 0;
    private ScopedSnapshot? CompleteScope(HistoryPage m)
    {
        if (!ScopedCapable || _baselineId == Guid.Empty) return null;
        var f = m.Scoped;
        if (!ValidScope(f) || string.IsNullOrWhiteSpace(f!.SnapshotId) || f.SnapshotId.Length > 160
            || _liveScope == null || f.PositionRevision != _liveScope.PositionRevision || f.PendingOrderRevision != _liveScope.PendingOrderRevision
            || m.PendingOrders == null || m.PendingOrders.Count > 10000 || m.PendingOrders.Any(x => x.Id <= 0)
            || m.PendingOrders.Select(x => x.Id).Distinct().Count() != m.PendingOrders.Count
            || !m.PendingOrders.OrderBy(x => x.Id).SequenceEqual(_pendingOrders.OrderBy(x => x.Id))
            || m.OpeningObservations == null || m.OpeningObservations.Count > 100000 || m.RecoveredResults == null || m.RecoveredResults.Count > 1000
            || _requestPendingRevision != _pendingRevision || _requestBrokerEpoch != BrokerEpoch) throw new InvalidOperationException("invalid scoped capture");
        var account = m.Account!;
        var received = DateTime.UtcNow;
        var receipt = new ScopedReceipt
        {
            Identity = new RecoveryIdentity { Platform = Platform, Broker = account.Broker, AccountNumber = account.Number,
                Currency = account.Currency, IsLive = account.IsLive, IsHedging = account.Hedging, Label = f.Label,
                ExecutionPath = RecoveryExecutionPath.DesktopBridge, SafetyVersion = f.SafetyVersion },
            BaselineId = f.BaselineId, RequestId = m.RequestId, ReceiptId = Id + ":" + f.SnapshotId, TransportGeneration = Id,
            BrokerGeneration = f.BrokerEpoch, BrokerConnected = true, TransportComplete = true,
            CapturedUtc = BridgeProtocol.FromUnixMs(m.CapturedAt), ReceivedUtc = received,
            PositionRevision = _positionsRevision, PendingOrderRevision = _pendingRevision,
            Positions = m.Positions!.Select(x => x.ToInfo()).ToArray(), PendingOrders = m.PendingOrders.Select(x => new PendingOrderObservation(x.Id, x.Label)).ToArray(),
            Closed = _historyRows.Select(x => x.ToInfo()).ToArray(), OpeningObservations = m.OpeningObservations.ToArray(),
        };
        return new ScopedSnapshot(receipt, f, m.RecoveredResults.ToArray(), MonotonicNow());
    }
    internal bool IsCurrent(ScopedReceipt receipt)
    {
        lock (_lock)
        {
            var a = _acceptedScope;
            return a != null && SourceInventoryReady && !_historyFailed && ValidScope(a.Source)
                && DeploymentInventory != null && DeploymentInventory.All(x => x.Healthy && !x.Missing && (x.Label == receipt.Identity.Label || x.Entries == 0))
                && receipt.Identity == a.Receipt.Identity && receipt.BaselineId == a.Receipt.BaselineId
                && receipt.RequestId == a.Receipt.RequestId && receipt.ReceiptId == a.Receipt.ReceiptId
                && receipt.CapturedUtc == a.Receipt.CapturedUtc && receipt.ReceivedUtc == a.Receipt.ReceivedUtc
                && receipt.TransportGeneration == Id && receipt.BrokerGeneration == BrokerEpoch
                && receipt.PositionRevision == _positionsRevision && receipt.PendingOrderRevision == _pendingRevision
                && a.Source.PositionRevision == _liveScope?.PositionRevision && a.Source.PendingOrderRevision == _liveScope?.PendingOrderRevision
                && MonotonicNow() - a.ReceivedMonotonic + (a.Receipt.ReceivedUtc - a.Receipt.CapturedUtc) < TimeSpan.FromSeconds(30);
        }
    }
    internal ScopeFrame? DispatchScope(ScopedReceipt receipt)
    { lock (_lock) return IsCurrent(receipt) ? _acceptedScope!.Source : null; }
    internal (bool sent, string? response) RequestScopedOpen(OpenCmd command, Func<bool> authorized, TimeSpan timeout) =>
        RequestAsync(command, command.Id, timeout, authorized).GetAwaiter().GetResult();
    internal (bool sent, string? response) RequestScopedPartial(CloseCmd command, Func<bool> authorized, TimeSpan timeout) =>
        RequestAsync(command, command.Id, timeout, authorized).GetAwaiter().GetResult();
}
