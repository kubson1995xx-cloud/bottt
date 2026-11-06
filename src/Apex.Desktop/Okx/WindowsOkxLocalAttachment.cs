using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Process-only trusted capability, not configuration, enrollment or a transfer receipt.
// Only an already native-bound sealed owner can enter here. No baseline creation.
[System.Text.Json.Serialization.JsonConverter(typeof(OkxSecretJsonRefusal<WindowsOkxLocalAttachment>))]
internal sealed class WindowsOkxLocalAttachment
{
    internal OkxScopeBinding Binding { get; }
    internal string Fingerprint { get; }
    private WindowsOkxLocalAttachment(OkxScopeBinding binding,string fingerprint){Binding=binding;Fingerprint=fingerprint;}
    internal static bool RemoteProfileAbsent(string root)=>!File.Exists(Path.Combine(root,"controller-connection.json"))&&!File.Exists(Path.Combine(root,"controller-pairing.pending.json"));
    internal static async Task<WindowsOkxLocalAttachment?> RequestAsync(string root,OkxScopeBinding binding,OkxActivationPolicy activation,long generation,CancellationToken ct,object commitSync,Func<bool> current)
    {
        if(!OperatingSystem.IsWindows()||binding.Account.Environment!=NavalEnvironment.OkxDemo||!RemoteProfileAbsent(root))return null;
        // Native DPAPI and exact physical origin are proven BEFORE asking for consent.
        using var credential=new WindowsOkxCredentialStore(root).Open(binding,OkxConfiguration.Paths(root,binding));
        if(credential.Provenance!="DemoTradingApi")return null;
        using var store=OkxNavalStore.OpenNative(root,binding);store.Admit(false);
        var origin=new NavalBrokerBinding(binding.Account,binding.MainUid,binding.ScopeId,binding.ConfigDigest,binding.CredentialBindingId,credential.Fingerprint,binding.SafetyVersion,store.StorageIdentity);
        using var owner=NavalBrokerSession.OpenExisting(store,origin,new NativeClock());
        if(owner.ReadState().Naval!.Mode!="STOP")return null;
        var request=new OkxActivationRequest(binding,credential.Fingerprint,Guid.NewGuid(),generation,OkxActivationPurpose.LocalAttachment,DateTimeOffset.UtcNow.AddSeconds(120));
        var lease=await activation.RequestAsync(request,ct).ConfigureAwait(false);
        lock(commitSync)
        {
            if(lease==null||ct.IsCancellationRequested||!current()||!RemoteProfileAbsent(root)||!activation.TryConsume(lease,request,DateTimeOffset.UtcNow))return null;
            // Store/owner lock remains held across approval; immutable origin is never edited.
            store.Admit(false);_ = owner.ReadState();
            return new(binding,credential.Fingerprint);
        }
    }
    private sealed class NativeClock:INavalBrokerClock
    {public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;public long Timestamp=>System.Diagnostics.Stopwatch.GetTimestamp();public long TimestampFrequency=>System.Diagnostics.Stopwatch.Frequency;}
}
