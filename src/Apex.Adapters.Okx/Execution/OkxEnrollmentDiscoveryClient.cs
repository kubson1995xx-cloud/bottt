using System.Collections.Immutable;
namespace Apex.Adapters.Okx.Execution;

// Observation-only identity; deliberately has no quote, execution scope or authority.
public sealed record OkxEnrollmentObservation(string Uid,string MainUid,string Environment,string Provenance,
    string Fingerprint,DateTimeOffset ObservedUtc,ImmutableArray<OkxExecutionBalanceDetailDto> Balances,
    ImmutableArray<OkxExecutionInstrumentDto> Instruments);
public sealed record OkxEnrollmentDiscovery(OkxEnrollmentObservation? Observation,string Code);
public static class OkxEnrollmentDiscoveryClient
{
    public static Task<OkxEnrollmentDiscovery> DiscoverAsync(string environment,string provenance,ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,string? expectedUid,string? expectedMainUid,CancellationToken ct)
        =>RunAsync(environment,provenance,key,secret,passphrase,expectedUid,expectedMainUid,ct,null);
    internal static Task<OkxEnrollmentDiscovery> RunAsync(string environment,string provenance,ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,string? expectedUid,string? expectedMainUid,CancellationToken ct,HttpMessageHandler? handler)
        =>Task.FromResult(new OkxEnrollmentDiscovery(null,"DISCOVERY_NOT_IMPLEMENTED"));
}
