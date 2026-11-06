using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
namespace Apex.Core.Naval;

// Parent-origin factual premise, NOT an exchange observation or execution capability.
public sealed record NavalFiniteOperatorPremise(string Schema,string Policy,string TestId,string CredentialRecordDigest,
    string Statement,string Provenance,DateTimeOffset IssuedUtc,DateTimeOffset ExpiresUtc,string Environment);
public sealed record NavalFundingAmount(string Raw,decimal? Value);
public sealed record NavalMarketFundingCurrency(string Currency,NavalFundingAmount UsedLimit,NavalFundingAmount Interest,
    NavalFundingAmount InterestFreeLiability,NavalFundingAmount PotentialBorrowingAmount);
// Historical observations may be sealed. A live request/START capability is never serialized.
public sealed record CurrentMarketFundingEvidenceV1(string Schema,string Scope,string AllProductClearance,string Environment,
    string Uid,string MainUid,string CredentialBindingId,string CredentialFingerprint,Guid Connection,Guid Capture,
    NavalBrokerSurface Response,long TimestampFrequency,NavalFundingAmount DebtUsd,NavalFundingAmount InterestUsd,
    int RecordCount,IReadOnlyList<NavalMarketFundingCurrency> Records);
internal static class NavalCurrentMarketFunding
{
    internal const string Statement="Nie będę wykonywać innych operacji podczas\nWirtualne środki i nie korzystałem z pożyczek";
    internal const string TestId="current-market-funding-bounded-demo-v1";
    // Exactly two origins. Linux records keep the parent value; the Windows value is
    // minted only by the native local-enrollment confirmation (WindowsOkxLocalEnrollment).
    internal const string ParentProvenance="parent-confirmed-user";
    internal const string WindowsLocalProvenance="windows-local-user-confirmed";
    internal static bool Premise(NavalFiniteOperatorPremise? p,DateTimeOffset at)=>p!=null&&p.Schema=="finite-operator-premise-v1"&&p.Policy=="current-market-funding-v1"&&
        p.TestId==TestId&&p.Environment=="OkxDemo"&&p.Statement==Statement&&(p.Provenance==ParentProvenance||p.Provenance==WindowsLocalProvenance)&&Hash(p.CredentialRecordDigest)&&
        p.IssuedUtc.Offset==TimeSpan.Zero&&p.ExpiresUtc.Offset==TimeSpan.Zero&&p.ExpiresUtc>p.IssuedUtc&&p.ExpiresUtc-p.IssuedUtc<=TimeSpan.FromMinutes(30)&&p.IssuedUtc<=at&&at<p.ExpiresUtc;
    private static bool Hash(string? s)=>s!=null&&Regex.IsMatch(s,"\\A[a-f0-9]{64}\\z");
    private static bool Zero(NavalFundingAmount? a)=>a!=null&&a.Value==0&&Regex.IsMatch(a.Raw,"\\A-?0+(?:\\.0+)?\\z");
    // Unknown raw debt remains unknown. Any KNOWN nonzero (including excluded) vetoes.
    internal static bool RawDebtCompatible(NavalBrokerBalance b)=>new[]{b.Liability,b.Interest,b.CrossLiability,b.IsolatedLiability}.All(v=>v==null||v==0);
    internal static bool Validate(CurrentMarketFundingEvidenceV1? e,NavalFiniteOperatorPremise? p,IReadOnlyList<NavalBrokerBalance> balances,
        string uid,string main,string credential,string fingerprint,Guid connection,Guid capture)
        =>ValidateCore(e,p,balances,uid,main,credential,fingerprint,connection,capture,false);
    // Historical origin remains the unchanged baseline premise. Expiry is not
    // renewed and this helper cannot issue entry/START permission. Current signed
    // amounts and both clocks still veto the dedicated reduction observation.
    internal static bool ValidateOwnedRecovery(CurrentMarketFundingEvidenceV1? e,NavalFiniteOperatorPremise? p,IReadOnlyList<NavalBrokerBalance> balances,
        string uid,string main,string credential,string fingerprint,Guid connection,Guid capture)
        =>ValidateCore(e,p,balances,uid,main,credential,fingerprint,connection,capture,true);
    // Release-owned bounded DEMO cash-SPOT policy. No operator premise is renewed,
    // and NOT_ESTABLISHED remains the explicit all-product evidence scope.
    internal static bool ValidateOrdinary(CurrentMarketFundingEvidenceV1? e,IReadOnlyList<NavalBrokerBalance> balances,
        string uid,string main,string credential,string fingerprint,Guid connection,Guid capture)
        =>ValidateCore(e,null,balances,uid,main,credential,fingerprint,connection,capture,false,true);
    private static bool ValidateCore(CurrentMarketFundingEvidenceV1? e,NavalFiniteOperatorPremise? p,IReadOnlyList<NavalBrokerBalance> balances,
        string uid,string main,string credential,string fingerprint,Guid connection,Guid capture,bool historical,bool ordinary=false)
    {
        if(e==null||e.Schema!="current-market-funding-v1"||e.Scope!="MARKET_LOANS_ONLY"||e.AllProductClearance!="NOT_ESTABLISHED"||e.Environment!="Demo"||
            uid!=e.Uid||main!=e.MainUid||uid!=main||credential!=e.CredentialBindingId||fingerprint!=e.CredentialFingerprint||!Hash(fingerprint)||
            connection==Guid.Empty||capture==Guid.Empty||connection!=e.Connection||capture!=e.Capture||e.TimestampFrequency<=0||
            e.Response.Endpoint!="/api/v5/account/interest-limits"||e.Response.Query!="?type=2"||e.Response.Rows!=1||!Hash(e.Response.Digest)||
            e.Response.RequestUtc.Offset!=TimeSpan.Zero||e.Response.ReceiveUtc.Offset!=TimeSpan.Zero||e.Response.ReceiveUtc<e.Response.RequestUtc||
            e.Response.ReceiveTimestamp<e.Response.RequestTimestamp||e.Response.ReceiveUtc-e.Response.RequestUtc>=TimeSpan.FromSeconds(30)||
            ((decimal)e.Response.ReceiveTimestamp-e.Response.RequestTimestamp)/e.TimestampFrequency>=30||
            !(ordinary|| (historical?p!=null&&Premise(p,p.IssuedUtc)&&p.IssuedUtc<=e.Response.RequestUtc:Premise(p,e.Response.RequestUtc)&&Premise(p,e.Response.ReceiveUtc)))||!Zero(e.DebtUsd)||!Zero(e.InterestUsd)||
            e.RecordCount is <1 or >256||e.Records.Count!=e.RecordCount||e.Records.Select(r=>r.Currency).Distinct(StringComparer.Ordinal).Count()!=e.RecordCount||
            e.Records.Any(r=>!Regex.IsMatch(r.Currency,"\\A[A-Z0-9]{1,20}\\z")||!Zero(r.UsedLimit)||!Zero(r.Interest)||!Zero(r.InterestFreeLiability)||!Zero(r.PotentialBorrowingAmount))||
            balances.Count is <1 or >256||balances.Select(r=>r.Currency).Distinct(StringComparer.Ordinal).Count()!=balances.Count||balances.Any(b=>!RawDebtCompatible(b)))return false;
        return true;
    }
    internal static bool Fresh(CurrentMarketFundingEvidenceV1 e,NavalFiniteOperatorPremise p,DateTimeOffset now,long mono,long frequency,
        DateTimeOffset captureUtc,long captureTimestamp)=>frequency==e.TimestampFrequency&&frequency>0&&Premise(p,now)&&
        e.Response.RequestUtc>=captureUtc&&e.Response.RequestTimestamp>=captureTimestamp&&now>=e.Response.ReceiveUtc&&mono>=e.Response.ReceiveTimestamp&&
        now-e.Response.RequestUtc<TimeSpan.FromSeconds(30)&&((decimal)mono-e.Response.RequestTimestamp)/frequency<30;
    internal static bool Balance(NavalBrokerBalance v,bool market)=>market?NavalBrokerValidation.CashFiniteBalance(v)&&RawDebtCompatible(v):NavalBrokerValidation.CleanFiniteBalance(v);
    internal static bool FreshOwnedRecovery(CurrentMarketFundingEvidenceV1 e,DateTimeOffset now,long mono,long frequency,DateTimeOffset captureUtc,long captureTimestamp)=>
        frequency==e.TimestampFrequency&&frequency>0&&e.Response.RequestUtc>=captureUtc&&e.Response.RequestTimestamp>=captureTimestamp&&
        now>=e.Response.ReceiveUtc&&mono>=e.Response.ReceiveTimestamp&&now-e.Response.RequestUtc<TimeSpan.FromSeconds(30)&&((decimal)mono-e.Response.RequestTimestamp)/frequency<30;
}
