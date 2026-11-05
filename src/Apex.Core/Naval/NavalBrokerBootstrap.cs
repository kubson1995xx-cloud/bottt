using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

internal interface INavalBrokerStore : IKeyValueStore
{
    string DirectoryPath { get; }
    string StorageIdentity { get; }
    void Admit(bool create);
}
// No financial owner exists until an exact, registered, separately approved candidate is consumed.
internal sealed class NavalBrokerBootstrap
{
    private readonly NavalBrokerBinding binding;
    private readonly INavalBrokerClock clock;
    private readonly INavalProtectedBaselineApproval provider;
    private readonly Dictionary<NavalBaselineCandidate,NavalBrokerObservationBatch> candidates=new();
    private readonly Dictionary<NavalBaselineApproval,NavalBaselineCandidate> approvals=new();
    private volatile bool closed;
    private bool pending;
    private DateTimeOffset lastUtc;
    private long lastTimestamp;
    private NavalBrokerObservationBatch? consumed;
    private readonly NavalFiniteBaselineAuthority? finiteAuthority;
    internal NavalBrokerBootstrap(NavalBrokerBinding binding,INavalBrokerClock clock,INavalProtectedBaselineApproval provider,NavalFiniteBaselineAuthority? finiteAuthority=null)
    {this.binding=binding;this.clock=clock;this.provider=provider;this.finiteAuthority=finiteAuthority;}
    private string keyDigest="";
    internal NavalBaselineCandidate ConsiderBaseline(NavalBrokerObservationBatch observations,string protectedKeyScopeDigest)
    {
        if(closed||candidates.Count!=0||protectedKeyScopeDigest.Length!=64)throw NavalBrokerValidation.Invalid();
        if(observations.FiniteAllocation!=null&&(finiteAuthority?.Binding!=binding||finiteAuthority.Connection!=observations.Connection))throw NavalBrokerValidation.Invalid();
        _=NavalBrokerValidation.ValidateBatch(binding,observations);
        var snapshot=NavalJson.Clone(observations);RequireFresh(snapshot);keyDigest=protectedKeyScopeDigest;
        var candidate=new NavalBaselineCandidate();candidates.Add(candidate,snapshot);return candidate;
    }
    private void RequireFresh(NavalBrokerObservationBatch o)
    {
        var now=clock.UtcNow;var mono=clock.Timestamp;
        if(closed||clock.TimestampFrequency<=0||now<lastUtc||mono<lastTimestamp||now<o.CaptureUtc||mono<o.CaptureTimestamp||now-o.CaptureUtc>=TimeSpan.FromSeconds(30)||
            (decimal)(mono-o.CaptureTimestamp)/clock.TimestampFrequency>=30||o.ServerUtc>now||now-o.ServerUtc>=TimeSpan.FromSeconds(30)||
            System.Linq.Enumerable.Any(o.Balances,b=>b.SourceUtc>now))throw new InvalidOperationException("BASELINE_EXPIRED");
        if(o.CurrentMarketFunding is {} funding&&!NavalCurrentMarketFunding.Fresh(funding,o.OperatorPremise!,now,mono,clock.TimestampFrequency,o.CaptureUtc,o.CaptureTimestamp))throw new InvalidOperationException("BASELINE_FUNDING_EXPIRED");
        // Currency uTime is last-change provenance, not response freshness. The original
        // value stays in the digest/bookends; capture, server and monotonic budgets expire.
        lastUtc=now;lastTimestamp=mono;
    }
    internal async ValueTask<NavalBaselineApproval?> RequestApprovalAsync(NavalBaselineCandidate candidate,CancellationToken ct)
    {
        if(pending||closed||!candidates.TryGetValue(candidate,out var o))return null;
        RequireFresh(o);pending=true;
        var request=new NavalBaselineApprovalRequest(NavalBrokerValidation.Digest(binding,o,keyDigest),binding.Account,NavalBrokerValidation.ValidateBatch(binding,o),
            binding.Account.QuoteCurrency,o.CatalogDigest,o.FeeDigest,keyDigest,binding.StorageBindingDigest,o.Connection,o.CaptureUtc.AddSeconds(30));
        bool accepted;
        try{accepted=await provider.ApproveAsync(request,ct).ConfigureAwait(false);}
        finally{pending=false;}
        RequireFresh(o);if(!accepted||ct.IsCancellationRequested)return null;
        var approval=new NavalBaselineApproval();approvals.Add(approval,candidate);return approval;
    }
    internal NavalBrokerSession InitializeBaseline(NavalBaselineCandidate candidate,NavalBaselineApproval approval,INavalBrokerStore store)
    {
        if(!candidates.TryGetValue(candidate,out var o))throw NavalBrokerValidation.Invalid();
        try
        {
            var owner=NavalCoreStateOwner.CreateBroker(store,this,candidate,approval);
            try{RequireFresh(o);closed=true;return new NavalBrokerSession(owner,owner.Read().NavalBroker!.Baseline,clock);}
            catch{owner.Dispose();throw;}
        }
        catch{closed=true;throw;}
    }
    internal NavalBrokerBaseline Consume(NavalBaselineCandidate candidate,NavalBaselineApproval approval,INavalBrokerStore store)
    {
        if(closed||!approvals.Remove(approval,out var registered)||registered!=candidate||!candidates.Remove(candidate,out var o)||store.StorageIdentity!=binding.StorageBindingDigest)throw NavalBrokerValidation.Invalid();
        RequireFresh(o);
        consumed=o;
        return new(NavalBrokerValidation.Digest(binding,o,keyDigest),binding,NavalBrokerValidation.ValidateBatch(binding,o),o,keyDigest,clock.UtcNow,clock.Timestamp);
    }
    internal void VerifyPersistence()=>RequireFresh(consumed??throw NavalBrokerValidation.Invalid());
    internal void Revoke(){closed=true;candidates.Clear();approvals.Clear();}
}
internal sealed partial class NavalBrokerSession : IDisposable
{
    private readonly NavalCoreStateOwner owner;
    internal NavalPortfolioCoordinator Coordinator { get; }
    internal NavalBrokerSession(NavalCoreStateOwner owner,NavalBrokerBaseline baseline,INavalBrokerClock clock)
    {this.owner=owner;Coordinator=new NavalPortfolioCoordinator(owner,NavalBrokerValidation.Definition(baseline),clock,baseline);}
    internal static NavalBrokerSession OpenExisting(INavalBrokerStore store,NavalBrokerBinding binding,INavalBrokerClock clock)
    {
        var owner=NavalCoreStateOwner.OpenBroker(store,binding);
        try{return new(owner,owner.Read().NavalBroker!.Baseline,clock);}catch{owner.Dispose();throw;}
    }
    internal void Disconnect(){Coordinator.Stop("DISCONNECTED");}
    public void Dispose()=>owner.Dispose();
}
