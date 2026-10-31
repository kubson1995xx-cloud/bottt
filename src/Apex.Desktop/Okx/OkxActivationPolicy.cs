using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;
internal enum OkxActivationPurpose { Observe, Execute }
internal sealed record OkxActivationRequest(OkxScopeBinding Binding, string CredentialFingerprint, Guid Session, long Generation, OkxActivationPurpose Purpose, DateTimeOffset ExpiresUtc);
internal interface IOkxProtectedActivationProvider { ValueTask<bool> ConfirmAsync(OkxActivationRequest request, CancellationToken cancellation); }
internal sealed class UnavailableOkxActivationProvider : IOkxProtectedActivationProvider
{
    public ValueTask<bool> ConfirmAsync(OkxActivationRequest request, CancellationToken cancellation) => ValueTask.FromResult(false);
}
[JsonConverter(typeof(OkxSecretJsonRefusal<OkxActivationLease>))]
internal sealed class OkxActivationLease
{
    private OkxActivationLease() { }
    // Only the owning policy's issuer can produce a lease in its private set.
    internal sealed class Issuer { internal OkxActivationLease Issue() => new(); }
}
internal sealed class OkxActivationPolicy
{
    private readonly object sync = new();
    private readonly IOkxProtectedActivationProvider provider;
    private readonly Func<DateTimeOffset> clock;
    private readonly OkxActivationLease.Issuer issuer = new();
    private readonly Dictionary<OkxActivationLease, (OkxActivationRequest Request, DateTimeOffset Issued, long Epoch)> outstanding = new();
    private long epoch;
    private bool pending;
    private bool closed;
    internal OkxActivationPolicy(IOkxProtectedActivationProvider provider, Func<DateTimeOffset> clock) { this.provider = provider; this.clock = clock; }
    internal async ValueTask<OkxActivationLease?> RequestAsync(OkxActivationRequest request, CancellationToken cancellation)
    {
        long generation; DateTimeOffset before = clock();
        if (cancellation.IsCancellationRequested || !Valid(request, before)) return null;
        lock (sync)
        {
            if (closed || pending) return null;
            pending = true; generation = epoch;
        }
        bool accepted;
        try { accepted = await provider.ConfirmAsync(request, cancellation); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        finally { lock (sync) pending = false; }
        lock (sync)
        {
            var now = clock();
            if (closed || !accepted || cancellation.IsCancellationRequested || generation != epoch || now < before || !Valid(request, now)) return null;
            // Remove stale capabilities and bound retained state before issuing.
            var expired = new List<OkxActivationLease>();
            foreach (var p in outstanding) if (p.Value.Request.ExpiresUtc <= now) expired.Add(p.Key);
            foreach (var lease in expired) outstanding.Remove(lease);
            if (outstanding.Count >= 64) return null;
            var issued = issuer.Issue(); outstanding.Add(issued, (request, before, epoch)); return issued;
        }
    }
    internal bool TryConsume(OkxActivationLease lease, OkxActivationRequest expected, DateTimeOffset now)
    {
        lock (sync)
        {
            if (lease == null || !outstanding.TryGetValue(lease, out var bound) || bound.Epoch != epoch || bound.Request != expected || now < bound.Issued || now >= bound.Request.ExpiresUtc) return false;
            outstanding.Remove(lease); return true;
        }
    }
    internal void Revoke() { lock (sync) { epoch++; outstanding.Clear(); } }
    internal void Close() { lock (sync) { closed = true; epoch++; outstanding.Clear(); } }
    private static bool Valid(OkxActivationRequest request, DateTimeOffset now)
    {
        if (request == null || request.Binding == null || request.Session == Guid.Empty || request.Generation < 0 ||
            request.Purpose is not (OkxActivationPurpose.Observe or OkxActivationPurpose.Execute) ||
            !OkxCredentialUseRegistry.FingerprintValid(request.CredentialFingerprint) || request.ExpiresUtc <= now || request.ExpiresUtc - now > TimeSpan.FromSeconds(120)) return false;
        try { OkxConfiguration.ValidateBinding(request.Binding); return true; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NullReferenceException) { return false; }
    }
}
