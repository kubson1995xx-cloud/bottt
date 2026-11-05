using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Release-coded interpretation of the approved conditional OKX protocol, NOT an atomicity
// certificate or operator permission. The durable authenticated baseline binds its account,
// environment, credentials, catalog and fee schedule; live private rechecks remain mandatory.
internal sealed class OkxConditionalCashSpotPolicy
{
    internal const string Id="OKX-CASH-FOK-RECEIVED-OR-QUOTE-FEE-CONDITIONAL-v2";
    private OkxConditionalCashSpotPolicy(){}
    internal static OkxConditionalCashSpotPolicy? Admit(NavalBrokerBaseline baseline)=>
        baseline.Observations.Account.FeeType is "0" or "1"&&
        baseline.Observations.Fees.EntryInBase==(baseline.Observations.Account.FeeType=="0")&&
        !baseline.Observations.Fees.ExitInBase
            ?new():null;
    internal const string Refusal="CONDITIONAL_POLICY_REQUIRES_MATCHING_RECEIVED_OR_QUOTE_FEES";
}
