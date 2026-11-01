using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Release-coded interpretation of the approved conditional OKX protocol, NOT an atomicity
// certificate or operator permission. The durable authenticated baseline binds its account,
// environment, credentials, catalog and fee schedule; live private rechecks remain mandatory.
internal sealed class OkxConditionalCashSpotPolicy
{
    internal const string Id="OKX-CASH-FOK-QUOTE-FEE-CONDITIONAL-v1";
    private OkxConditionalCashSpotPolicy(){}
    internal static OkxConditionalCashSpotPolicy? Admit(NavalBrokerBaseline baseline)=>
        baseline.Observations.Account.FeeType=="1"&&!baseline.Observations.Fees.EntryInBase&&!baseline.Observations.Fees.ExitInBase
            ?new():null;
    internal const string Refusal="CONDITIONAL_POLICY_REQUIRES_FEE_TYPE_1_QUOTE_ONLY";
}
