using System.Text.Json;
using Apex.Core.Engine;
namespace Apex.Okx.DemoAcceptance;

// Shared by the executable and its synthetic offline integration proof. Projection
// preserves observed balances separately from accepted ledger legs and raw fills.
internal static class TransactionReport
{
    internal static byte[] Build(string runId,DateTimeOffset started,bool complete,string result,string[] stages,EngineState? state,object? discoveryDiagnostic,object? transport,bool armed,object? selection=null,string operation="FINITE_DEMO_SEQUENCE")
    {
        var report=new{schema="apex-okx-finite-demo-v1",operation,runId,startedUtc=started,finishedUtc=complete?DateTimeOffset.UtcNow:(DateTimeOffset?)null,complete,result,environment="DEMO",host="https://eea.okx.com:443",schedulerStarted=false,armed,stages,
            exposureCap=10,quote=state?.NavalBroker?.Baseline.Binding.Account.QuoteCurrency,windowsGuiCredUiDpapi="NOT_EXERCISED",identity=Program.BinaryIdentity(),discoveryDiagnostic,transport,
            financialScope=new{kind="ONE_NOMINATED_QUOTE_AND_OWN_TEST_INVENTORY_NOT_FULL_ACCOUNT_EQUITY",allocation=state?.NavalBroker?.Baseline.Observations.FiniteAllocation,includedQuoteCash=state?.NavalBroker?.Baseline.InitialCash,excludedObservedHoldings=state?.NavalBroker?.Baseline.Observations.FiniteAllocation?.Excluded,excludedSpendOrSellAuthority=false},selection,
            funding=new{scope=state?.NavalBroker?.Baseline.Observations.CurrentMarketFunding?.Scope,allProductClearance="NOT_ESTABLISHED",operatorPremiseSource="PARENT_OPERATOR_NOT_EXCHANGE_VERIFIED",current=state?.NavalBroker?.Operational?.LastCurrentMarketFunding,
                uncoveredBalanceCurrencies=state?.NavalBroker?.Operational?.LastBalances?.Where(v=>state.NavalBroker.Operational.LastCurrentMarketFunding!=null&&!state.NavalBroker.Operational.LastCurrentMarketFunding.Records.Any(r=>r.Currency==v.Currency)).Select(v=>v.Currency).ToArray(),missingCurrencyRecordsAreZero=false},
            baseline=state?.NavalBroker?.Baseline,orders=state?.NavalBroker?.Operational?.Orders,fills=state?.NavalBroker?.Operational?.FillEvidence,bills=state?.NavalBroker?.Operational?.Bills,protection=state?.NavalBroker?.Operational?.Protection,
            intents=state?.NavalBroker?.Operational?.Intents,settledIntents=state?.NavalBroker?.Operational?.SettledIntents,obligations=state?.NavalBroker?.Operational?.Obligations,
            reservations=state?.Naval?.Reservations,mode=state?.Naval?.Mode??"STOP",cash=state?.Naval?.Accounts,
            quoteCashDelta=state?.Naval==null?(decimal?)null:state.Naval.Accounts.Single().Cash-state.NavalBroker!.Baseline.InitialCash,
            residualBase=state?.Naval?.Reservations.Select(r=>new{r.Intent.Instrument,r.Inventory,r.Pending,r.ReductionUnsettled}).ToArray(),
            observedBalances=state?.NavalBroker?.Operational?.LastBalances,balanceCaptureUtc=state?.NavalBroker?.Operational?.LastBalanceCaptureUtc,
            balanceDeltas=state?.NavalBroker?.Operational?.LastBalances?.Select(v=>new{v.Currency,before=state.NavalBroker.Baseline.Observations.Balances.SingleOrDefault(b=>b.Currency==v.Currency)?.Cash??0,after=v.Cash,netDelta=v.Cash-(state.NavalBroker.Baseline.Observations.Balances.SingleOrDefault(b=>b.Currency==v.Currency)?.Cash??0),
                acceptedLedgerGrossLeg=state.Naval!.Fills.Sum(p=>v.Currency==state.NavalBroker.Baseline.Binding.Account.QuoteCurrency?(p.Fill.Reduction?1:-1)*p.Fill.Quantity*p.Fill.Price:state.NavalBroker.Baseline.Observations.Catalog.Single(i=>i.Instrument==p.Fill.Instrument).BaseCurrency==v.Currency?(p.Fill.Reduction?-1:1)*p.Fill.Quantity:0),
                acceptedLedgerFeeLeg=-state.Naval.Fills.Where(p=>p.Fill.FeeCurrency==v.Currency).Sum(p=>p.Fill.Fee)}).ToArray(),
            privacy="Native user/host encrypted local credentials at rest; hidden controlling SSH TTY only for save/replace, default load without TTY. No secret argv/env/plaintext files; no proxy/redirect; core/dumpability disabled; active swap/tracer refused. Storage is not trading authority. Same-UID/root/full-host compromise, SSH-client recording and erasure of framework/TLS/native copies are outside the guarantee.",
            recovery="STOP is not liquidation. Preserve this report and exact authoritative state. Do not restart/reset to repeat a buy. Unknown exposure/protection/pending orders require exact-ID review and separately bounded cleanup."};
        return JsonSerializer.SerializeToUtf8Bytes(report,new JsonSerializerOptions{WriteIndented=true});
    }
}
