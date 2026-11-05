using System.Collections.Immutable;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Current Market-loan quantities only. USD aggregates are not currency capital;
/// future accrual schedules are not snapshot timestamps. No financial capability.</summary>
public sealed record OkxMarketLoansDto(OkxDecimal DebtUsd,OkxDecimal InterestUsd,OkxTimestamp NextDiscountTime,
    OkxTimestamp NextInterestTime,ImmutableArray<OkxMarketLoanCurrencyDto> Records)
{ public override string ToString()=>"OKX Market-loan observation [REDACTED]"; }
public sealed record OkxMarketLoanCurrencyDto(string Currency,OkxDecimal UsedLimit,OkxDecimal Interest,
    OkxDecimal InterestFreeLiability,OkxDecimal PotentialBorrowingAmount)
{ public override string ToString()=>"OKX Market-loan currency [REDACTED]"; }

internal static partial class OkxObservationJson
{
    // Regional Crawl4AI SHA94574685..., lines 4185–4311. Only type=2 is exposed.
    private static OkxMarketLoansDto MarketLoans(JsonElement r)
    {
        Shape(r,"debt interest loanAlloc nextDiscountTime nextInterestTime",arrays:"records");
        var records=Required(r,"records","Array");
        if(records.ValueKind!=JsonValueKind.Array)throw new JsonException();
        if(records.GetArrayLength()>256)throw new CardinalityException();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var output=ImmutableArray.CreateBuilder<OkxMarketLoanCurrencyDto>();
        foreach(var row in records.EnumerateArray())
        {
            // Deprecated fields remain strictly typed, not financial inputs.
            if(row.ValueKind!=JsonValueKind.Object)throw new JsonException();
            foreach(var p in row.EnumerateObject())
                if(p.Name=="surplusLmtDetails")Shape(p.Value,"allAcctRemainingQuota curAcctRemainingQuota platRemainingQuota");
                else if(!"ccy rate loanQuota surplusLmt usedLmt interest interestFreeLiab potentialBorrowingAmt posLoan availLoan usedLoan avgRate".Split(' ').Contains(p.Name,StringComparer.Ordinal)||p.Value.ValueKind!=JsonValueKind.String)throw new JsonException();
            string ccy=S(row,"ccy");
            if(!Grammar.Match(ccy,"[A-Z0-9]{1,20}")||!seen.Add(ccy))throw new JsonException();
            output.Add(new(ccy,Nonnegative(row,"usedLmt"),Nonnegative(row,"interest"),Nonnegative(row,"interestFreeLiab"),Nonnegative(row,"potentialBorrowingAmt")));
        }
        return new(Nonnegative(r,"debt"),Nonnegative(r,"interest"),Timestamp(r,"nextDiscountTime"),Timestamp(r,"nextInterestTime"),output.ToImmutable());
    }
    private static OkxDecimal Nonnegative(JsonElement r,string name)
    {var n=Number(r,name);if(n.Value<0)throw new JsonException();return n;}
}
