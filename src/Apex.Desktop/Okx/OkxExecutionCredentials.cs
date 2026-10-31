using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Naval;

namespace Apex.Desktop.Okx;
internal interface IOkxExecutionSecretProvider { OkxExecutionCredentialLease Open(OkxScopeBinding binding, OkxScopePaths paths); }

// Owned byte buffers, not a serializable credential DTO. Callers must not retain
// spans past Dispose; signing will be synchronous over these spans in Task2.
[JsonConverter(typeof(OkxSecretJsonRefusal<OkxExecutionCredentialLease>))]
internal sealed class OkxExecutionCredentialLease : IDisposable
{
    private readonly byte[] key, secret, passphrase;
    private bool disposed;
    internal OkxScopeBinding Binding { get; }
    internal string Provenance { get; }
    internal string Fingerprint { get; }
    internal ReadOnlySpan<byte> ApiKey { get { ThrowDisposed(); return key; } }
    internal ReadOnlySpan<byte> Secret { get { ThrowDisposed(); return secret; } }
    internal ReadOnlySpan<byte> Passphrase { get { ThrowDisposed(); return passphrase; } }
    internal OkxExecutionCredentialLease(OkxScopeBinding binding, string provenance, byte[] key, byte[] secret, byte[] passphrase)
    {
        this.key = key; this.secret = secret; this.passphrase = passphrase;
        try
        {
            OkxConfiguration.ValidateBinding(binding);
            if (provenance != (binding.Account.Environment == NavalEnvironment.OkxDemo ? "DemoTradingApi" : "LiveAccountApi") ||
                key is not { Length: > 0 and <= 4096 } || secret is not { Length: > 0 and <= 4096 } || passphrase is not { Length: > 0 and <= 4096 }) throw new InvalidDataException("OKX_CREDENTIAL_INVALID");
            Binding = binding; Provenance = provenance;
            Fingerprint = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        }
        catch { Dispose(); throw new InvalidDataException("OKX_CREDENTIAL_INVALID"); }
    }
    private void ThrowDisposed() { if (disposed) throw new ObjectDisposedException(nameof(OkxExecutionCredentialLease)); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (key != null) CryptographicOperations.ZeroMemory(key);
        if (secret != null) CryptographicOperations.ZeroMemory(secret);
        if (passphrase != null) CryptographicOperations.ZeroMemory(passphrase);
    }
}
internal sealed class OkxSecretJsonRefusal<T> : JsonConverter<T>
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException("OKX_PRIVATE_CAPABILITY");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => throw new NotSupportedException("OKX_PRIVATE_CAPABILITY");
}
internal sealed class OkxCredentialUseRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, OkxScopeBinding> bindings = new(StringComparer.Ordinal);
    internal static bool FingerprintValid(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal bool TryBind(OkxScopeBinding binding, string fingerprint)
    {
        if (!FingerprintValid(fingerprint)) return false;
        try { OkxConfiguration.ValidateBinding(binding); } catch { return false; }
        lock (sync)
        {
            if (bindings.TryGetValue(fingerprint, out var existing)) return existing == binding;
            bindings.Add(fingerprint, binding); return true;
        }
    }
}

// A bounded binary envelope avoids immutable base64/secret strings in managed
// JSON. All fields are fixed/order-closed and authenticated by scoped DPAPI.
internal static class OkxCredentialEnvelope
{
    internal const string Schema = "okx-execution-credential-v1";
    internal static byte[] Encode(OkxExecutionCredentialLease lease)
    {
        // Decode bounds envelopes to 64 KiB; validated metadata and three fields
        // of at most 4096 bytes fit within it. A fixed array-backed stream cannot
        // grow and abandon plaintext copies. Allocate before any secret writes.
        byte[] owned = new byte[65536];
        try
        {
            using var stream = new MemoryStream(owned, 0, owned.Length, writable: true, publiclyVisible: true);
            stream.SetLength(0);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Schema); writer.Write(JsonSerializer.Serialize(lease.Binding)); writer.Write(lease.Provenance); writer.Write(lease.Fingerprint);
            Write(writer, lease.ApiKey); Write(writer, lease.Secret); Write(writer, lease.Passphrase); writer.Flush();
            return stream.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }
    private static void Write(BinaryWriter writer, ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    internal static OkxExecutionCredentialLease Decode(byte[] plaintext, OkxScopeBinding expected)
    {
        byte[]? key = null, secret = null, passphrase = null; OkxExecutionCredentialLease? lease = null;
        try
        {
            if (plaintext.Length is <= 0 or > 65536) throw new InvalidDataException();
            using var stream = new MemoryStream(plaintext, writable: false); using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadString() != Schema) throw new InvalidDataException();
            // Exact producer metadata bytes: no unknown/duplicate/case-folded fields
            // can be smuggled into an otherwise matching immutable binding.
            if (reader.ReadString() != JsonSerializer.Serialize(expected)) throw new InvalidDataException();
            string provenance = reader.ReadString(), fingerprint = reader.ReadString();
            key = Read(reader); secret = Read(reader); passphrase = Read(reader);
            if (stream.Position != stream.Length) throw new InvalidDataException();
            lease = new(expected, provenance, key, secret, passphrase);
            if (lease.Fingerprint != fingerprint) throw new InvalidDataException();
            return lease;
        }
        catch
        {
            lease?.Dispose();
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (secret != null) CryptographicOperations.ZeroMemory(secret);
            if (passphrase != null) CryptographicOperations.ZeroMemory(passphrase);
            throw new InvalidDataException("OKX_CREDENTIAL_INVALID");
        }
    }
    private static byte[] Read(BinaryReader reader)
    {
        int size = reader.ReadInt32(); if (size is <= 0 or > 4096 || reader.BaseStream.Length - reader.BaseStream.Position < size) throw new InvalidDataException();
        return reader.ReadBytes(size);
    }
}
