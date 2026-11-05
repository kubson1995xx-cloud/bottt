using Apex.Adapters.Okx.Execution;
namespace Apex.Okx.DemoAcceptance;

// Friend-only test executable composition. Not a CLI/config/environment authority.
// Main always passes null. This type and delegates are internal and never minted by
// input data. Production financial, Linux store, signer and parser stay unchanged.
internal sealed record FiniteEntryTestComposition(TimeProvider Clock,
    Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> Reads,
    Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationStreamFence,OkxObservationStream> Streams,
    Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxFinalDispatchFence,OkxExecutionClient> Writes,
    System.Net.Http.HttpMessageHandler Market);
