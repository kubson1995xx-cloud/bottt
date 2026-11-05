using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Adapters.Okx;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal static class OkxCurrentMarketFunding
{
    internal static async Task<CurrentMarketFundingEvidenceV1> ReadAsync(OkxExecutionObservationClient client,OkxObservationContext context,
        NavalFiniteOperatorPremise premise,string fingerprint,IReadOnlyList<NavalBrokerBalance> balances,TimeProvider clock,CancellationToken ct)
        =>await ReadCoreAsync(client,context,premise,fingerprint,balances,clock,ct,false).ConfigureAwait(false);
    internal static Task<CurrentMarketFundingEvidenceV1> ReadOwnedRecoveryAsync(OkxExecutionObservationClient client,OkxObservationContext context,
        NavalFiniteOperatorPremise historical,string fingerprint,IReadOnlyList<NavalBrokerBalance> balances,TimeProvider clock,CancellationToken ct)
        =>ReadCoreAsync(client,context,historical,fingerprint,balances,clock,ct,true);
    internal static Task<CurrentMarketFundingEvidenceV1> ReadOrdinaryAsync(OkxExecutionObservationClient client,OkxObservationContext context,
        string fingerprint,IReadOnlyList<NavalBrokerBalance> balances,TimeProvider clock,CancellationToken ct)
        =>ReadCoreAsync(client,context,null,fingerprint,balances,clock,ct,false,true);
    private static async Task<CurrentMarketFundingEvidenceV1> ReadCoreAsync(OkxExecutionObservationClient client,OkxObservationContext context,
        NavalFiniteOperatorPremise? premise,string fingerprint,IReadOnlyList<NavalBrokerBalance> balances,TimeProvider clock,CancellationToken ct,bool historical,bool ordinary=false)
    {
        ct.ThrowIfCancellationRequested();var start=clock.GetUtcNow();long mono=clock.GetTimestamp();
        if(!ordinary&&!NavalCurrentMarketFunding.Premise(premise,historical?premise!.IssuedUtc:start))throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_REQUIRED");
        var r=await client.ReadMarketLoansAsync(context,ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();
        if(r.Status!=OkxObservationStatus.Observed||r.Context!=context||r.Rows.Count!=1||r.ResponseDigest==null||r.RequestedScope.Environment!="Demo")throw new OkxFiniteAccountRefusal("FINITE_CURRENT_MARKET_UNPROVEN");
        var d=r.Rows[0];var s=r.RequestedScope;
        static NavalFundingAmount Amount(OkxDecimal v)=>new(v.Raw??"",v.Value);
        var evidence=new CurrentMarketFundingEvidenceV1("current-market-funding-v1","MARKET_LOANS_ONLY","NOT_ESTABLISHED",s.Environment,s.Uid,s.MainUid,s.CredentialBindingId,fingerprint,
            context.ConnectionId,context.CaptureId,new(r.Endpoint,r.ExactQuery,r.ResponseDigest,r.Rows.Count,r.RequestUtc,r.ReceiveUtc,r.RequestMonotonic,r.ReceiveMonotonic),clock.TimestampFrequency,
            Amount(d.DebtUsd),Amount(d.InterestUsd),d.Records.Length,d.Records.Select(v=>new NavalMarketFundingCurrency(v.Currency,Amount(v.UsedLimit),Amount(v.Interest),Amount(v.InterestFreeLiability),Amount(v.PotentialBorrowingAmount))).ToArray());
        bool valid=ordinary?NavalCurrentMarketFunding.ValidateOrdinary(evidence,balances,s.Uid,s.MainUid,s.CredentialBindingId,fingerprint,context.ConnectionId,context.CaptureId)&&NavalCurrentMarketFunding.FreshOwnedRecovery(evidence,clock.GetUtcNow(),clock.GetTimestamp(),clock.TimestampFrequency,start,mono):historical?NavalCurrentMarketFunding.ValidateOwnedRecovery(evidence,premise,balances,s.Uid,s.MainUid,s.CredentialBindingId,fingerprint,context.ConnectionId,context.CaptureId)&&NavalCurrentMarketFunding.FreshOwnedRecovery(evidence,clock.GetUtcNow(),clock.GetTimestamp(),clock.TimestampFrequency,start,mono):
            NavalCurrentMarketFunding.Validate(evidence,premise,balances,s.Uid,s.MainUid,s.CredentialBindingId,fingerprint,context.ConnectionId,context.CaptureId)&&NavalCurrentMarketFunding.Fresh(evidence,premise,clock.GetUtcNow(),clock.GetTimestamp(),clock.TimestampFrequency,start,mono);
        if(!valid)throw new OkxFiniteAccountRefusal("FINITE_CURRENT_MARKET_INVALID_OR_ENCUMBERED");
        return evidence;
    }
}
