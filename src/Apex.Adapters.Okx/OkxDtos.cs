using System.Collections.Immutable;
namespace Apex.Adapters.Okx;
public sealed record OkxDecimal(string? Raw, decimal? Value);
public sealed record OkxTimestamp(string? Raw, DateTimeOffset? Utc);
public sealed record OkxTimeDto(OkxTimestamp Ts);
public sealed record OkxAccountConfigDto(string Uid, string MainUid, string AcctLv, string Perm, string Ip, string FeeType, bool AutoLoan, bool EnableSpotBorrow)
{ public override string ToString() => "OKX account config [REDACTED]"; }
public sealed record OkxBalanceDetailDto(string Ccy, OkxDecimal CashBal, OkxDecimal AvailBal, OkxDecimal FrozenBal, OkxDecimal OrdFrozen, OkxDecimal Eq, OkxDecimal Liab);
public sealed record OkxBalanceDto(OkxTimestamp UTime, ImmutableArray<OkxBalanceDetailDto> Details);
public sealed record OkxInstrumentDto(string InstId, string InstType, string BaseCcy, string QuoteCcy, ImmutableArray<string> TradeQuoteCcyList, string State, OkxDecimal TickSz, OkxDecimal LotSz, OkxDecimal MinSz, OkxTimestamp ListTime, OkxTimestamp ExpTime, string? GroupId, ImmutableDictionary<string, string?> MaxValues);
public sealed record OkxOrderDto(string InstType, string InstId, string OrdId, string? ClOrdId, string State, string Side, string TdMode, OkxDecimal Sz, OkxDecimal AccFillSz, OkxDecimal AvgPx, OkxTimestamp CTime, OkxTimestamp UTime, string? TradeQuoteCcy);
public sealed record OkxFillDto(string InstType, string InstId, string OrdId, string? ClOrdId, string TradeId, string BillId, string Side, OkxDecimal FillPx, OkxDecimal FillSz, OkxDecimal Fee, string FeeCcy, OkxDecimal FeeRate, string? ExecType, OkxTimestamp FillTime, OkxTimestamp Ts, string? TradeQuoteCcy);
public sealed record OkxBillDto(string BillId, string Ccy, OkxDecimal Bal, OkxDecimal BalChg, OkxDecimal Fee, string? OrdId, string? TradeId, string? InstId, string Type, string SubType, OkxTimestamp Ts);
