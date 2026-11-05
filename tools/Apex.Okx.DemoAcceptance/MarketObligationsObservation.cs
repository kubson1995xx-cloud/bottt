using System.Text.Json;
using Apex.Adapters.Okx.Execution;
namespace Apex.Okx.DemoAcceptance;

// Closed diagnostic collector. Does not reference financial selection, baseline, runtime or execution.
internal static class MarketObligationsObservation
{
    internal static async Task<object> CollectAsync(OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string fingerprint,
        Func<bool> safe,CancellationToken caller,Action<object> publish,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient>? factory=null,TimeProvider? time=null)
    {
        var clock=time??TimeProvider.System;var start=clock.GetUtcNow();long mono=clock.GetTimestamp();
        var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(caller);budget.CancelAfter(TimeSpan.FromSeconds(90));
        var fence=new ReadFence(scope,context,safe,clock,budget.Token,start,mono);
        await using var reads=(factory??OkxExecutionObservationClient.Create)(scope,provider,fingerprint,fence);
        var trace=new List<object>();OkxExecutionBalanceDto? before=null,after=null;OkxMarketLoansDto? current=null;
        string stage="ACCOUNT_BEFORE",result="OBSERVATION_UNPROVEN";bool consistent=false;
        object Snapshot()=>new{result,stage,requestUtc=start,receiveUtc=clock.GetUtcNow(),requestMonotonic=mono,receiveMonotonic=clock.GetTimestamp(),monotonicFrequency=clock.TimestampFrequency,
            context.ConnectionId,context.CaptureId,bracketUnchanged=consistent&&after!.UTime==before!.UTime,
            selectedBalanceRowsConsistent=consistent,aggregateBalanceTimestampChanged=before is null||after is null?(bool?)null:after.UTime!=before.UTime,
            consistencyPolicy="BOUNDED_LOCAL_SELECTED_BALANCE_DTO_V1",readTrace=trace.ToArray(),http=reads.ReadDiagnostics(),balancesBefore=before,balancesAfter=after,marketLoans=current,
            coverage=current is null?null:new{recordsReturned=current.Records.Length,emptyRecords=current.Records.Length==0,
                missingBalanceCurrencies=before?.Details.Select(x=>x.Currency).Except(current.Records.Select(x=>x.Currency),StringComparer.Ordinal).ToArray(),
                returnedCurrenciesOutsideBalances=current.Records.Select(x=>x.Currency).Except(before?.Details.Select(x=>x.Currency)??[],StringComparer.Ordinal).ToArray(),
                aggregateMoneyAvailable=current.DebtUsd.Value.HasValue&&current.InterestUsd.Value.HasValue,
                currencyMoneyAvailable=current.Records.All(r=>r.UsedLimit.Value.HasValue&&r.Interest.Value.HasValue&&r.InterestFreeLiability.Value.HasValue&&r.PotentialBorrowingAmount.Value.HasValue),
                positiveAggregate=current.DebtUsd.Value>0||current.InterestUsd.Value>0,
                positiveCurrencyAmount=current.Records.Any(r=>r.UsedLimit.Value>0||r.Interest.Value>0||r.InterestFreeLiability.Value>0||r.PotentialBorrowingAmount.Value>0),
                usdToCurrencyConsistency="NOT_COMPARABLE_WITHOUT_CONVERSION",allAccountCoverage="UNKNOWN",scheduleTimesAreFreshness=false}};
        void Trace<T>(OkxObservationRead<T> r)=>trace.Add(new{r.Endpoint,r.ExactQuery,status=r.Status.ToString(),r.ErrorCode,r.RequestUtc,r.ReceiveUtc,r.RequestMonotonic,r.ReceiveMonotonic,r.ResponseDigest});
        T One<T>(OkxObservationRead<T> r)
        {Trace(r);if(r.Status!=OkxObservationStatus.Observed||r.Rows.Count!=1)throw new ReadRefusal(r.ErrorCode??"CARDINALITY");return r.Rows[0];}
        bool Account(OkxExecutionAccountDto a)=>scope.Environment=="Demo"&&scope.Uid==scope.MainUid&&a.Uid==scope.Uid&&a.MainUid==scope.MainUid&&a.Permissions=="read_only,trade"&&a.AccountLevel=="1"&&a.AccountType=="0"&&a.SpotRoleType=="0"&&!a.AutoLoan&&!a.EnableSpotBorrow&&!a.SpotBorrowAutoRepay&&!string.IsNullOrWhiteSpace(a.Ip);
        // uTime is last-update provenance, not collection start. Bound it by this read's receive,
        // never by a later wall clock; idle old sources and updates during the read remain raw.
        bool Balance(OkxExecutionBalanceDto b,DateTimeOffset receive)=>b.Details.Length is >=1 and <=256&&b.Details.Select(x=>x.Currency).Distinct(StringComparer.Ordinal).Count()==b.Details.Length&&
            b.UTime.Utc<=receive&&b.Details.All(x=>x.UTime.Utc<=receive);
        try
        {
            var a=One(await reads.ReadAccountAsync(context,budget.Token).ConfigureAwait(false));if(!Account(a))throw new ReadRefusal("ACCOUNT_POLICY_REFUSED");publish(Snapshot());
            stage="BALANCE_BEFORE";var beforeRead=await reads.ReadBalancesAsync(context,budget.Token).ConfigureAwait(false);before=One(beforeRead);if(!Balance(before,beforeRead.ReceiveUtc))throw new ReadRefusal("BALANCE_SHAPE_OR_FUTURE_SOURCE");publish(Snapshot());
            stage="MARKET_LOANS";current=One(await reads.ReadMarketLoansAsync(context,budget.Token).ConfigureAwait(false));publish(Snapshot());
            stage="ACCOUNT_AFTER";var last=One(await reads.ReadAccountAsync(context,budget.Token).ConfigureAwait(false));if(!Account(last)||last!=a)throw new ReadRefusal("ACCOUNT_BOOKEND_MOVED");publish(Snapshot());
            stage="BALANCE_AFTER";var afterRead=await reads.ReadBalancesAsync(context,budget.Token).ConfigureAwait(false);after=One(afterRead);
            // Bounded local selected-row consistency, NOT provider atomicity or whole-account clearance.
            // Keep every row field/raw clock exact. Aggregate account-update provenance may move;
            // retain both raw timestamps and report bracketUnchanged=false in that case.
            if(!Balance(after,afterRead.ReceiveUtc)||!after.Details.OrderBy(x=>x.Currency,StringComparer.Ordinal).SequenceEqual(before.Details.OrderBy(x=>x.Currency,StringComparer.Ordinal)))throw new ReadRefusal("BALANCE_BOOKEND_MOVED");
            if(!fence.CanObserve(scope,context,clock.GetUtcNow()))throw new ReadRefusal("EXPIRED_OR_STOPPED");
            consistent=true;stage="FINISHED";result="MARKET_LOANS_OBSERVED_STOP_NO_TRADING";
        }
        catch(ReadRefusal e){result=e.Code;}
        catch(OperationCanceledException){result="CANCELED_OR_BUDGET_EXPIRED";}
        var final=Snapshot();publish(final);return final;
    }
    private sealed class ReadRefusal(string code):Exception {internal string Code {get;}=code;}
    private sealed class ReadFence(OkxExecutionScope scope,OkxObservationContext context,Func<bool> safe,TimeProvider clock,CancellationToken ct,DateTimeOffset start,long mono):IOkxObservationReadFence
    {
        public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset now)=>s==scope&&c==context&&!ct.IsCancellationRequested&&safe()&&
            now>=start&&now-start<TimeSpan.FromSeconds(90)&&clock.GetTimestamp()>=mono&&clock.GetElapsedTime(mono)<TimeSpan.FromSeconds(90);
    }
    internal static byte[] Report(string runId,DateTimeOffset started,bool complete,string result,string[] stages,object? discovery,object? observation)=>JsonSerializer.SerializeToUtf8Bytes(new{
        schema="apex-okx-market-obligations-v1",operation="CHECK_OBLIGATIONS_GET_ONLY",runId,startedUtc=started,reportUtc=DateTimeOffset.UtcNow,complete,result,stages,
        requestedEndpoint="/api/v5/account/interest-limits?type=2",loanType="2",scope="MARKET_LOANS_ONLY",AllAccountClearance="NOT_ESTABLISHED",TradingAuthorityGranted=false,
        mode="STOP",armed=false,schedulerStarted=false,financialOwner=(object?)null,baseline=(object?)null,orders=(object?)null,intents=(object?)null,
        identity=Program.BinaryIdentity(),discovery,observation},new JsonSerializerOptions{WriteIndented=true});
}
