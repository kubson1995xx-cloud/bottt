using System.Collections.Generic;
using Apex.Adapters.Okx.Execution;
namespace Apex.Desktop.Okx;

// Shared exact/pending selected-operand proof, never transport or execution authority.
internal static class OkxPendingProtectionProof
{
    internal static IReadOnlyList<string> Problems(OkxExecutionAlgoDto exact,OkxExecutionAlgoDto pending)
    {
        var problems=new List<string>();
        // A pending observation cannot corroborate terminal exact detail, even if
        // it repeats that terminal state. Neither endpoint's digest supersedes the other.
        if(exact.State!="live"||pending.State!="live")problems.Add("OWNED_PENDING_PROTECTION_LIFECYCLE_CONFLICT_NO_SELL");
        if(pending.OrdIdList is not {Length:0}||pending.SubAlgoIdList is {Length:>0}||pending.AttachAlgoOrds is {Length:>0}||pending.AdvChaseParams is {Length:>0}||
            !string.IsNullOrEmpty(pending.OrdId)||!string.IsNullOrEmpty(pending.LinkedOrd?.OrdId)||pending.ActualSz.Value is not (null or 0)||pending.ActualPx.Value is not (null or 0)||!string.IsNullOrEmpty(pending.ActualSide)||pending.TriggerTime.Utc!=null)
            problems.Add("OWNED_PENDING_PROTECTION_CHILD_OR_RELATION_UNPROVEN_NO_SELL");
        if(pending.InstType!=exact.InstType||pending.InstId!=exact.InstId||pending.AlgoId!=exact.AlgoId||pending.AlgoClOrdId!=exact.AlgoClOrdId||pending.ClOrdId!=exact.ClOrdId||
            pending.Side!=exact.Side||pending.TdMode!=exact.TdMode||pending.TradeQuoteCcy!=exact.TradeQuoteCcy||pending.OrdType!=exact.OrdType||pending.Sz.Value!=exact.Sz.Value||
            pending.SlTriggerPx.Value!=exact.SlTriggerPx.Value||pending.TpTriggerPx.Value!=exact.TpTriggerPx.Value||pending.SlOrdPx.Value!=exact.SlOrdPx.Value||pending.TpOrdPx.Value!=exact.TpOrdPx.Value||
            pending.SlTriggerPxType!=exact.SlTriggerPxType||pending.TpTriggerPxType!=exact.TpTriggerPxType)
            problems.Add("OWNED_PENDING_PROTECTION_IDENTITY_OR_ECONOMICS_CONFLICT_NO_SELL");
        // Only the common selected configuration is comparable. Market last,
        // observation clocks, labels and endpoint-specific opaque metadata are not it.
        if(pending.PosSide!=exact.PosSide||pending.TgtCcy!=exact.TgtCcy||pending.Ccy!=exact.Ccy||pending.ReduceOnly!=exact.ReduceOnly||pending.IsTradeBorrowMode!=exact.IsTradeBorrowMode||
            pending.AmendPxOnTriggerType!=exact.AmendPxOnTriggerType||pending.QuickMgnType!=exact.QuickMgnType||pending.FailCode is not (null or "" or "0")||
            !string.IsNullOrEmpty(pending.AdvanceOrdType)||!string.IsNullOrEmpty(pending.TriggerPxType)||!string.IsNullOrEmpty(pending.ChaseType)||!string.IsNullOrEmpty(pending.MaxChaseType)||
            !string.IsNullOrEmpty(pending.CloseFraction.Raw)||!string.IsNullOrEmpty(pending.TriggerPx.Raw)||!string.IsNullOrEmpty(pending.OrdPx.Raw)||!string.IsNullOrEmpty(pending.CallbackRatio.Raw)||
            !string.IsNullOrEmpty(pending.CallbackSpread.Raw)||!string.IsNullOrEmpty(pending.ActivePx.Raw)||!string.IsNullOrEmpty(pending.MoveTriggerPx.Raw)||!string.IsNullOrEmpty(pending.ChaseVal.Raw)||!string.IsNullOrEmpty(pending.MaxChaseVal.Raw))
            problems.Add("OWNED_PENDING_PROTECTION_CONFIGURATION_CONFLICT_NO_SELL");
        return problems;
    }
}
