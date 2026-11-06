using System;
using System.Collections.Generic;
using System.Linq;
namespace Apex.Core.Naval;

// Additive ordinary-only scope, not a new baseline/allocation. Null omission keeps
// every previously sealed owner byte-canonical until this explicit STOP transition.
public sealed record NavalOrdinaryCatalog(string Schema,string BaselineDigest,string AdmissionDigest,
    Guid Session,DateTimeOffset ObservedUtc,string ObservationDigest,IReadOnlyList<NavalInstrument> Instruments);
internal sealed partial class NavalBrokerSession
{
    internal NavalRiskResult AdmitOrdinaryCatalog(IReadOnlyList<NavalInstrument> instruments,DateTimeOffset utc,long timestamp,string digest)
        =>Coordinator.AdmitOrdinaryCatalog(this,instruments,utc,timestamp,digest);
}
internal static class NavalOrdinaryCatalogPolicy
{
    internal static IReadOnlyList<NavalInstrument> Catalog(NavalBrokerState b)=>b.Operational?.OrdinaryCatalog?.Instruments??b.Baseline.Observations.Catalog;
    internal static NavalPortfolioDefinition Definition(NavalBrokerState b)
    {
        var original=NavalBrokerValidation.Definition(b.Baseline);
        return b.Operational?.OrdinaryCatalog==null?original:new(original.Id,original.Limits,original.Members,Catalog(b),original.Fees);
    }
    internal static bool Shape(NavalBrokerState b,IReadOnlyList<NavalInstrument> catalog)=>NavalOrdinaryDemoPolicy.FreshLocal(b)?FreshLocalShape(b,catalog):
        b.Baseline.Binding.Account.Environment==NavalEnvironment.OkxDemo&&b.Baseline.Binding.Account.QuoteCurrency=="EUR"&&
        b.Baseline.Observations.Catalog.Count==1&&b.Baseline.Observations.Catalog[0].Instrument=="SOL-EUR"&&
        catalog.Count is >=1 and <=3&&catalog.Select(i=>i.Instrument).Distinct(StringComparer.Ordinal).Count()==catalog.Count&&
        catalog.Select(i=>i.BaseCurrency).Distinct(StringComparer.Ordinal).Count()==catalog.Count&&
        b.Baseline.Observations.Catalog.All(catalog.Contains)&&
        catalog.All(i=>i.BaseCurrency is "BTC" or "ETH" or "SOL"&&i.Instrument==i.BaseCurrency+"-EUR"&&i.QuoteCurrency=="EUR"&&i.Quantum>0&&i.MinimumQuantity>0&&i.TickSize>0);
    // Fresh Windows-local trading catalog is exactly BTC/ETH/SOL-EUR. The single
    // unheld finite-origin anchor is kept only when it is one of those three.
    private static bool FreshLocalShape(NavalBrokerState b,IReadOnlyList<NavalInstrument> catalog)
    {
        var anchor=b.Baseline.Observations.Catalog;
        return b.Baseline.Binding.Account.Environment==NavalEnvironment.OkxDemo&&b.Baseline.Binding.Account.QuoteCurrency=="EUR"&&anchor.Count==1&&
            catalog.Count==3&&catalog.Select(i=>i.Instrument).OrderBy(i=>i,StringComparer.Ordinal).SequenceEqual(NavalOrdinaryDemoPolicy.FreshLocalInstruments)&&
            catalog.All(i=>i.Instrument==i.BaseCurrency+"-EUR"&&i.QuoteCurrency=="EUR"&&i.Quantum>0&&i.MinimumQuantity>0&&i.TickSize>0)&&
            (!NavalOrdinaryDemoPolicy.FreshLocalInstruments.Contains(anchor[0].Instrument)||catalog.Contains(anchor[0]));
    }
    internal static void Validate(NavalBrokerState b)
    {
        if(b.Operational?.OrdinaryCatalog is not {} c)return;
        if(b.Operational.OrdinaryDemo==null||c.Schema!="ordinary-demo-eur-catalog-v1"||c.BaselineDigest!=b.Baseline.Digest||
            c.AdmissionDigest!=NavalJson.Hash(b.Operational.OrdinaryDemo)||c.Session==Guid.Empty||c.ObservedUtc.Offset!=TimeSpan.Zero||
            c.ObservationDigest.Length!=64||!c.ObservationDigest.All(Uri.IsHexDigit)||!Shape(b,c.Instruments))throw NavalBrokerValidation.Invalid();
    }
}
public sealed partial class NavalPortfolioCoordinator
{
    internal NavalRiskResult AdmitOrdinaryCatalog(NavalBrokerSession session,IReadOnlyList<NavalInstrument> catalog,DateTimeOffset utc,long timestamp,string digest)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational;var clock=(INavalBrokerClock)_clock;
            if(!BrokerSource(session)||_brokerAuthority?.OrdinaryDemoPolicy!=true||_brokerAuthority is not INavalFiniteDemoAuthority{FiniteDemo:false}||
                _brokerAuthority.CanExecute||s.Mode!="STOP"||_started||_frozen||x?.OrdinaryDemo==null||!BrokerFresh()||
                x.Obligations.Any(o=>NavalOrdinaryDemoPolicy.Blocking(e,o))||s.Reservations.Any(r=>NavalCashRiskPolicy.Active(r)))
                return Result(s,NavalMutationStatus.Refused,"ORDINARY_CATALOG_SETTLED_UNARMED_STOP_REQUIRED");
            if(x.OrdinaryCatalog!=null)return Result(s,NavalMutationStatus.Duplicate,"ORDINARY_CATALOG_ALREADY_ADMITTED");
            if(utc.Offset!=TimeSpan.Zero||utc>clock.UtcNow||clock.UtcNow-utc>=TimeSpan.FromSeconds(30)||timestamp>clock.Timestamp||
                (decimal)(clock.Timestamp-timestamp)/clock.TimestampFrequency>=30||digest.Length!=64||!digest.All(Uri.IsHexDigit)||!NavalOrdinaryCatalogPolicy.Shape(b,catalog))
                return Result(s,NavalMutationStatus.Refused,"ORDINARY_CATALOG_OBSERVATION_INVALID");
            var extension=new NavalOrdinaryCatalog("ordinary-demo-eur-catalog-v1",b.Baseline.Digest,NavalJson.Hash(x.OrdinaryDemo),_brokerAuthority.SessionId,utc,digest,NavalJson.Clone(catalog));
            e.NavalBroker=b with{Operational=x with{OrdinaryCatalog=extension}};
            // Catalog growth cannot preserve old quote coverage or START readiness.
            _ready=false;_started=false;_brokerContinuation=null;
            return Commit(e,s with{Mode="STOP",Reason="ORDINARY_CATALOG_FRESH_RECONCILIATION_REQUIRED",LiquidationEquity=null},NavalMutationStatus.Applied,"ORDINARY_CATALOG_ADMITTED",Maintenance());
        }
    }
}
