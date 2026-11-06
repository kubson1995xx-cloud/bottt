using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

// Fixed metadata file, separate from encrypted secrets and broker observations.
// Only the explicit parent provisioning command consumes the exact factual receipt.
internal static class FiniteOperatorReceipt
{
    internal const string Name="finite-operator-premise.json";
    private static readonly JsonSerializerOptions Options=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    internal static async Task<NavalFiniteOperatorPremise> LoadAsync(string root,LinuxDemoCredentialVault vault,CancellationToken ct)
    {
        string path=Path.Combine(root,Name);if(!File.Exists(path))throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_REQUIRED");OkxLinuxPrivate.AdmitFile(path);
        var bytes=File.ReadAllBytes(path);if(bytes.Length is <1 or >4096)throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_REQUIRED");
        var premise=Parse(bytes);
        if(premise.Provenance!=NavalCurrentMarketFunding.ParentProvenance||!NavalCurrentMarketFunding.Premise(premise,DateTimeOffset.UtcNow)||premise.CredentialRecordDigest!=await vault.RecordIdentityAsync(ct).ConfigureAwait(false))throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_INVALID_OR_WRONG_RECORD");
        return premise;
    }
    internal static async Task ProvisionAsync(string root,LinuxDemoCredentialVault vault,Stream input,CancellationToken ct)
    {
        // Explicit metadata, not an automatically minted confirmation. Caller supplies
        // all statement/provenance/test/scope/time fields, digest is tied to the record.
        var bytes=new byte[4097];int count=0;
        while(count<bytes.Length){int n=await input.ReadAsync(bytes.AsMemory(count),ct).ConfigureAwait(false);if(n==0)break;count+=n;}
        if(count is <1 or >4096)throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_INPUT_REFUSED");
        var p=Parse(bytes.AsSpan(0,count));
        // The Windows-local provenance is native-dialog-only and never accepted by this Linux CLI.
        if(p.Provenance!=NavalCurrentMarketFunding.ParentProvenance||p.CredentialRecordDigest!="PARENT_BIND_CURRENT_ENCRYPTED_RECORD")throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_INPUT_REFUSED");
        p=p with{CredentialRecordDigest=await vault.RecordIdentityAsync(ct).ConfigureAwait(false)};
        if(!NavalCurrentMarketFunding.Premise(p,DateTimeOffset.UtcNow))throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_INPUT_REFUSED");
        string path=Path.Combine(root,Name);
        // No silent replacement: a stale/wrong receipt needs explicit parent removal
        // after review; financial state is never deleted/reset by this command.
        using var f=new FileStream(path,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
        var output=JsonSerializer.SerializeToUtf8Bytes(p,Options);await f.WriteAsync(output,ct).ConfigureAwait(false);f.Flush(true);
    }
    private static NavalFiniteOperatorPremise Parse(ReadOnlySpan<byte> bytes)
    {
        using var doc=JsonDocument.Parse(bytes.ToArray());
        if(doc.RootElement.ValueKind!=JsonValueKind.Object||doc.RootElement.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=9||doc.RootElement.EnumerateObject().Count()!=9)throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_INPUT_REFUSED");
        return JsonSerializer.Deserialize<NavalFiniteOperatorPremise>(bytes,Options)??throw new OkxFiniteAccountRefusal("FINITE_OPERATOR_PREMISE_INPUT_REFUSED");
    }
}
