using System;
using System.Collections.Generic;
using System.Linq;
namespace Apex.Core.Naval;

// Comparison/persistence data, never financial or START capabilities.
public sealed record NavalBrokerBinding(NavalAccountId Account,string MainUid,string ScopeId,string ConfigDigest,
    string CredentialBindingId,string CredentialFingerprint,string SafetyVersion,string StorageBindingDigest);
public sealed record NavalBrokerBalance(string Currency,decimal? Cash,decimal? Available,decimal? Equity,decimal? Frozen,
    decimal? OrderFrozen,decimal? Liability,decimal? Interest,decimal? CrossLiability,decimal? IsolatedLiability,
    decimal? StrategyEquity,decimal? FixedBalance,DateTimeOffset? SourceUtc);
public sealed record NavalBrokerAccountObservation(string Uid,string MainUid,string Level,string Permissions,string Ip,
    string FeeType,bool AutoLoan,bool Borrow,bool AutoRepay,string Type,string SpotRole);
public sealed record NavalBrokerSurface(string Endpoint,string Query,string Digest,int Rows,DateTimeOffset RequestUtc,
    DateTimeOffset ReceiveUtc,long RequestTimestamp,long ReceiveTimestamp);
public sealed record NavalBrokerObservationBatch(Guid Connection,Guid Capture,Guid StreamEpoch,long StreamSequence,
    DateTimeOffset CaptureUtc,long CaptureTimestamp,DateTimeOffset ServerUtc,NavalBrokerAccountObservation Account,
    IReadOnlyList<NavalBrokerBalance> Balances,IReadOnlyList<NavalInstrument> Catalog,NavalFeeSchedule Fees,
    IReadOnlyList<NavalBrokerSurface> Surfaces,string CatalogDigest,string FeeDigest);
public sealed record NavalBrokerBaseline(string Digest,NavalBrokerBinding Binding,decimal InitialCash,
    NavalBrokerObservationBatch Observations,string KeyScopeDigest,DateTimeOffset ApprovedUtc,long ApprovedTimestamp);
public sealed record NavalBrokerRevision(long Revision,string Code);
public sealed record NavalBrokerState(string Schema,string ExecutionSchema,string Namespace,ulong HighWater,
    NavalBrokerBaseline Baseline,IReadOnlyList<NavalBrokerRevision> Revisions,string IntegrityDigest)
{
    // Absent in accepted clean broker-v2 files; first operational commit upgrades in place.
    // Older readers refuse this extension rather than dropping it or resetting the allocation.
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NavalBrokerOperationalState? Operational { get; init; }
}
internal interface INavalBrokerClock : INavalClock { long Timestamp { get; } long TimestampFrequency { get; } }
internal sealed record NavalBaselineApprovalRequest(string Digest,NavalAccountId Account,decimal Cash,string Quote,
    string CatalogDigest,string FeeDigest,string KeyScopeDigest,string RootIdentity,Guid Session,DateTimeOffset ExpiresUtc);
internal interface INavalProtectedBaselineApproval
{
    System.Threading.Tasks.ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,System.Threading.CancellationToken cancellation);
}
internal sealed class NavalBaselineCandidate { internal NavalBaselineCandidate() { } }
internal sealed class NavalBaselineApproval { internal NavalBaselineApproval() { } }

internal static class NavalBrokerValidation
{
    internal const string StateKey="naval-authority-broker-v2", Marker=".naval-broker-initialized-v2";
    internal static NavalPortfolioDefinition Definition(NavalBrokerBaseline b)=>new(new("okx-"+b.Binding.ScopeId,b.Binding.Account.QuoteCurrency),new(),
        new[]{new NavalSeed(b.Binding.Account,b.InitialCash)},b.Observations.Catalog,b.Observations.Fees);
    internal static NavalBrokerState Seal(NavalBrokerState b)=>b with{IntegrityDigest=NavalJson.Hash(b with{IntegrityDigest=""})};
    internal static void Definition(NavalPortfolioDefinition d,NavalBrokerBaseline b)
    {
        var account=b.Binding.Account;
        if(account.Venue!="OKX"||account.Environment is not (NavalEnvironment.OkxDemo or NavalEnvironment.Live)||account.AccountId!=b.Binding.MainUid||
            b.Binding.SafetyVersion!="okx-cash-v1"||b.InitialCash<=0||d.Digest!=Definition(b).Digest)throw new System.IO.InvalidDataException("BROKER_BINDING_INVALID");
        // Reuse all original limits/catalog/fee checks without admitting broker definitions to the Offline entry.
        var offline=new NavalPortfolioDefinition(d.Id,d.Limits,d.Members.Select(m=>new NavalSeed(m.Account with{Environment=NavalEnvironment.Offline},m.Allocation)),d.Catalog,d.Fees);
        NavalCashRiskPolicy.ValidateDefinition(offline);
    }
    internal static void Validate(Apex.Core.Engine.EngineState e,NavalPortfolioDefinition d)
    {
        var b=e.NavalBroker??throw Invalid();var s=e.Naval??throw Invalid();Definition(d,b.Baseline);
        NavalPortfolioCoordinator.ValidateBrokerOperational(e);
        NavalBrokerExecutionValidation.Validate(e);
        if(b.Schema!="naval-broker-v2"||b.ExecutionSchema!="naval-execution-broker-v2"||!Guid.TryParseExact(b.Namespace,"N",out _)||
            b.IntegrityDigest!=Seal(b).IntegrityDigest||e.NavalExecution!=null||e.NavalReadJournal!=null||
            b.Revisions.Count!=e.DurableRevision+1||b.Revisions.Where((r,n)=>r.Revision!=n||r.Code is not ("BASELINE_CREATED" or "DISCONNECTED" or "BROKER_STOP" or "BROKER_OPERATION")).Any()||
            (b.Operational==null&&(b.HighWater!=0||s.Mode!="STOP"||s.Reservations.Count!=0||s.Commands.Count!=0||s.Fills.Count!=0||s.Receipts.Count!=0||s.Generation!=0||s.Sequence!=0||s.ReconciledUtc!=null))||
            b.Baseline.Digest!=Digest(b.Baseline.Binding,b.Baseline.Observations,b.Baseline.KeyScopeDigest)||
            ValidateBatch(b.Baseline.Binding,b.Baseline.Observations)!=b.Baseline.InitialCash)throw Invalid();
    }
    internal static string Digest(NavalBrokerBinding b,NavalBrokerObservationBatch o,string key)=>NavalJson.Hash(new{Binding=b,Observations=o,KeyScopeDigest=key});
    internal static decimal ValidateBatch(NavalBrokerBinding b,NavalBrokerObservationBatch o)
    {
        var a=o.Account;
        if(o.Connection==Guid.Empty||o.Capture==Guid.Empty||o.StreamEpoch==Guid.Empty||o.StreamSequence<1||a.Uid!=b.Account.AccountId||a.MainUid!=b.MainUid||
            a.Level!="1"||a.AutoLoan||a.Borrow||a.AutoRepay||a.Type!="0"||a.SpotRole!="0"||string.IsNullOrWhiteSpace(a.Ip)||
            a.Permissions!="read_only,trade"||a.FeeType is not ("0" or "1")||o.Balances.Count!=1||o.Catalog.Count==0||
            o.CatalogDigest!=NavalJson.Hash(o.Catalog)||o.FeeDigest!=NavalJson.Hash(o.Fees))throw Invalid();
        var q=o.Balances[0];
        if(q.Currency!=b.Account.QuoteCurrency||q.Cash is not >0||q.Cash!=q.Available||q.Cash!=q.Equity||q.Frozen!=0||q.OrderFrozen!=0||
            q.Liability!=0||q.Interest!=0||q.CrossLiability!=0||q.IsolatedLiability!=0||q.StrategyEquity!=0||q.FixedBalance!=0||q.SourceUtc==null||
            o.Surfaces.Count<10||o.Surfaces.Any(s=>s.Digest.Length!=64||s.ReceiveUtc<s.RequestUtc||s.ReceiveTimestamp<s.RequestTimestamp))throw Invalid();
        return q.Cash.Value;
    }
    internal static System.IO.InvalidDataException Invalid()=>new("BROKER_STATE_INVALID_PRESERVED");
}
