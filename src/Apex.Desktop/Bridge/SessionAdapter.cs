using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Desktop.Bridge
{
    /// <summary>
    /// Adapter łączący silnik APEX z aktualną sesją mostu. Stabilny między ponownymi połączeniami:
    /// po rozłączeniu silnik widzi IsConnected = false (wstrzymuje działania), po powrocie – uzgadnia stan pozycji.
    /// Każde polecenie handlowe bez odpowiedzi po wysłaniu jest raportowane jako NIEPEWNE (silnik najpierw sprawdza pozycje).
    /// </summary>
    public sealed class SessionAdapter : IBrokerGateway, IBrokerHistory, IMarketData, IFeatureProvider, ITrustedSimulationGateway
    {
        private readonly bool _trustedSimulation;
        bool ITrustedSimulationGateway.TrustedSimulation => _trustedSimulation;
        private BridgeSession? _session;
        private readonly Func<CommissionOverride?> _commission;
        private readonly Func<bool> _canOpen;

        public SessionAdapter(Func<CommissionOverride?> commission) : this(commission, () => true) { }
        public SessionAdapter(Func<CommissionOverride?> commission, Func<bool> canOpen) : this(commission, canOpen, false) { }
        internal SessionAdapter(Func<CommissionOverride?> commission, Func<bool> canOpen, bool trustedSimulation)
        {
            _trustedSimulation = trustedSimulation;
            _commission = commission;
            _canOpen = canOpen;
        }

        public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan QuoteMaxAge { get; set; } = TimeSpan.FromMinutes(10);

        public BridgeSession? Session => Volatile.Read(ref _session);

        public void Attach(BridgeSession s) => Interlocked.Exchange(ref _session, s);

        public void Detach(BridgeSession s) => Interlocked.CompareExchange(ref _session, null, s);

        public bool IsConnected => Session?.HasFreshSnapshots == true;
        public BrokerHistorySnapshot GetHistory() => Session?.History ?? new();

        public AccountSnapshot GetAccount()
        {
            var session = Session;
            var a = session?.Account;
            return a == null ? new AccountSnapshot() : a.ToSnapshot(session!.AccountReceivedUtc);
        }

        public IReadOnlyList<PositionInfo> GetPositions()
        {
            var session = Session;
            return session == null ? Array.Empty<PositionInfo>() : session.Positions.Select(p => p.ToInfo()).ToList();
        }

        private static OrderOutcome MapResult(bool sent, string? json, string what)
        {
            if (!sent) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, $"{what}: brak połączenia z mostem – nie wysłano");
            if (json == null) return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, $"{what}: brak odpowiedzi mostu w czasie – stan niepewny");
            var r = BridgeProtocol.Deserialize<ResultMsg>(json);
            if (r == null) return OrderOutcome.Uncertain(BrokerErrorKind.Other, $"{what}: nieczytelna odpowiedź mostu");
            return ProtocolResult.ToOutcome(r);
        }

        public OrderOutcome OpenMarket(OrderRequest req)
        {
            if (!_canOpen() || !GetHistory().Complete || !GetHistory().RequiredCoverage)
                return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "Nowe wejścia zablokowane przez stan Desktop/historii.");
            var s = Session;
            if (s == null) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak sesji mostu");
            string id = string.IsNullOrEmpty(req.ExecutionId) ? s.NextId() : req.ExecutionId;
            var (sent, json) = s.Request(new OpenCmd
            {
                Id = id,
                SignalId = req.SignalId,
                Symbol = req.Symbol,
                Side = BridgeProtocol.SideOf(req.Direction),
                Units = req.VolumeUnits,
                SlPips = req.StopLossPips,
                TpPips = req.TakeProfitPips,
                Label = req.Label,
                Comment = req.Comment,
                MaxSlipPips = req.MaxSlippagePips,
            }, id, OrderTimeout);
            return MapResult(sent, json, "otwarcie");
        }

        public OrderOutcome ModifyStopLoss(long positionId, double stopLoss)
        {
            var s = Session;
            if (s == null) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak sesji mostu");
            string id = s.NextId();
            var (sent, json) = s.Request(new ModifyCmd { Id = id, PositionId = positionId, Sl = stopLoss }, id, OrderTimeout);
            return MapResult(sent, json, "zmiana SL");
        }

        public OrderOutcome ClosePosition(long positionId, double? units)
        {
            var s = Session;
            if (s == null) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak sesji mostu");
            string id = s.NextId();
            var (sent, json) = s.Request(new CloseCmd { Id = id, PositionId = positionId, Units = units }, id, OrderTimeout);
            return MapResult(sent, json, "zamknięcie");
        }

        public double EstimateMargin(string symbol, TradeDirection direction, double units)
        {
            var s = Session;
            if (s == null) return double.NaN;
            string id = s.NextId();
            var (sent, json) = s.Request(new MarginCmd { Id = id, Symbol = symbol, Side = BridgeProtocol.SideOf(direction), Units = units }, id, TimeSpan.FromSeconds(6));
            if (!sent || json == null) return double.NaN;
            var r = BridgeProtocol.Deserialize<MarginResultMsg>(json);
            return r?.Margin is double m && m >= 0 ? m : double.NaN;
        }

        public InstrumentSpec? GetSpec(string symbol)
        {
            var s = Session;
            var dto = s?.Symbol(symbol);
            if (dto == null) return null;
            var q = s!.Quote(symbol);
            return SpecMapper.ToSpec(dto, q?.q.Pv, _commission());
        }

        public Quote? GetQuote(string symbol)
        {
            var q = Session?.Quote(symbol);
            if (q == null) return null;
            var (dto, received) = q.Value;
            var t = dto.T > 0 ? BridgeProtocol.FromUnixMs(dto.T) : received;
            return new Quote(dto.B, dto.A, t);
        }

        public bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose)
        {
            timeToClose = TimeSpan.Zero;
            var q = Session?.Quote(symbol);
            if (q == null) return false;
            var (dto, received) = q.Value;
            if (!dto.O) return false;
            if (utc - received > QuoteMaxAge) return false;
            timeToClose = dto.Ttc >= 0 ? TimeSpan.FromSeconds(dto.Ttc) : TimeSpan.MaxValue;
            return true;
        }

        public DateTime? LastClosedBarTime(string symbol, Tf tf, DateTime utc) => Session?.Bars.LastClosedBarTime(symbol, tf, utc);

        public bool TryGet(string symbol, Tf tf, DateTime utc, out FeatureSet features, out int index)
        {
            var s = Session;
            if (s == null)
            {
                features = null!;
                index = -1;
                return false;
            }
            return s.Bars.TryGet(symbol, tf, utc, out features, out index);
        }
    }
}
