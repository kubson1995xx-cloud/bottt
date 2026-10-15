using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed class SessionAdapter : IBrokerGateway, IMarketData, IFeatureProvider
    {
        private BridgeSession? _session;
        private readonly Func<CommissionOverride?> _commission;

        public SessionAdapter(Func<CommissionOverride?> commission) => _commission = commission;

        public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan QuoteMaxAge { get; set; } = TimeSpan.FromMinutes(10);

        public BridgeSession? Session => _session;

        public void Attach(BridgeSession s) => _session = s;

        public void Detach(BridgeSession s)
        {
            if (ReferenceEquals(_session, s)) _session = null;
        }

        public bool IsConnected => _session?.IsAlive == true && _session.Account != null;

        public AccountSnapshot GetAccount()
        {
            var a = _session?.Account;
            return a == null ? new AccountSnapshot() : a.ToSnapshot(DateTime.UtcNow);
        }

        public IReadOnlyList<PositionInfo> GetPositions() =>
            _session == null ? Array.Empty<PositionInfo>() : _session.Positions.Select(p => p.ToInfo()).ToList();

        private static OrderOutcome MapResult(bool sent, string? json, string what)
        {
            if (!sent) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, $"{what}: brak połączenia z mostem – nie wysłano");
            if (json == null) return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, $"{what}: brak odpowiedzi mostu w czasie – stan niepewny");
            var r = BridgeProtocol.Deserialize<ResultMsg>(json);
            if (r == null) return OrderOutcome.Uncertain(BrokerErrorKind.Other, $"{what}: nieczytelna odpowiedź mostu");
            switch (r.Status?.ToLowerInvariant())
            {
                case "filled": return OrderOutcome.Filled(r.PositionId ?? 0, r.Price ?? 0);
                case "rejected": return OrderOutcome.Rejected(BridgeProtocol.ParseError(r.Error), r.Message ?? r.Error ?? "odrzucone");
                default: return OrderOutcome.Uncertain(BridgeProtocol.ParseError(r.Error), r.Message ?? "stan niepewny");
            }
        }

        public OrderOutcome OpenMarket(OrderRequest req)
        {
            var s = _session;
            if (s == null) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak sesji mostu");
            string id = s.NextId();
            var (sent, json) = s.Request(new OpenCmd
            {
                Id = id,
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
            var s = _session;
            if (s == null) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak sesji mostu");
            string id = s.NextId();
            var (sent, json) = s.Request(new ModifyCmd { Id = id, PositionId = positionId, Sl = stopLoss }, id, OrderTimeout);
            return MapResult(sent, json, "zmiana SL");
        }

        public OrderOutcome ClosePosition(long positionId, double? units)
        {
            var s = _session;
            if (s == null) return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak sesji mostu");
            string id = s.NextId();
            var (sent, json) = s.Request(new CloseCmd { Id = id, PositionId = positionId, Units = units }, id, OrderTimeout);
            return MapResult(sent, json, "zamknięcie");
        }

        public double EstimateMargin(string symbol, TradeDirection direction, double units)
        {
            var s = _session;
            if (s == null) return double.NaN;
            string id = s.NextId();
            var (sent, json) = s.Request(new MarginCmd { Id = id, Symbol = symbol, Side = BridgeProtocol.SideOf(direction), Units = units }, id, TimeSpan.FromSeconds(6));
            if (!sent || json == null) return double.NaN;
            var r = BridgeProtocol.Deserialize<MarginResultMsg>(json);
            return r?.Margin is double m && m >= 0 ? m : double.NaN;
        }

        public InstrumentSpec? GetSpec(string symbol)
        {
            var s = _session;
            var dto = s?.Symbol(symbol);
            if (dto == null) return null;
            var q = s!.Quote(symbol);
            return SpecMapper.ToSpec(dto, q?.q.Pv, _commission());
        }

        public Quote? GetQuote(string symbol)
        {
            var q = _session?.Quote(symbol);
            if (q == null) return null;
            var (dto, received) = q.Value;
            var t = dto.T > 0 ? BridgeProtocol.FromUnixMs(dto.T) : received;
            return new Quote(dto.B, dto.A, t);
        }

        public bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose)
        {
            timeToClose = TimeSpan.Zero;
            var q = _session?.Quote(symbol);
            if (q == null) return false;
            var (dto, received) = q.Value;
            if (!dto.O) return false;
            if (utc - received > QuoteMaxAge) return false;
            timeToClose = dto.Ttc >= 0 ? TimeSpan.FromSeconds(dto.Ttc) : TimeSpan.MaxValue;
            return true;
        }

        public DateTime? LastClosedBarTime(string symbol, Tf tf, DateTime utc) => _session?.Bars.LastClosedBarTime(symbol, tf, utc);

        public bool TryGet(string symbol, Tf tf, DateTime utc, out FeatureSet features, out int index)
        {
            var s = _session;
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
