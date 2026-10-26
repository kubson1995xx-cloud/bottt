using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Apex.Platforms.CTrader;

/// <summary>One LocalStorage value is the commit unit. The SDK does not promise multi-key
/// transactions: no shadow key/CSV key decides accounting. Torn/ambiguous values fail closed.
/// A valid previous or next envelope can be selected only by reading the authoritative key.</summary>
public static class RecoveryEnvelope
{
    private sealed class Envelope
    {
        public string Format { get; set; } = "apex-recovery-envelope-v1";
        public string Identity { get; set; } = "";
        public string Generation { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    private static string Digest(string identity, string generation, string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + generation + "\n" + payload)));
    public static string Encode(string identity, string payload)
    {
        using var validated = JsonDocument.Parse(payload);
        var e = new Envelope { Identity = identity, Generation = Guid.NewGuid().ToString("N"), Payload = payload };
        e.Sha256 = Digest(e.Identity, e.Generation, e.Payload);
        return JsonSerializer.Serialize(e);
    }
    public static string Decode(string identity, string stored)
    {
        using var parsed = JsonDocument.Parse(stored);
        if (!parsed.RootElement.TryGetProperty("Format", out _))
        {
            // Only explicit legacy engine state is migratable; never accept an unwrapped v2.
            if (parsed.RootElement.TryGetProperty("SchemaVersion", out var v) && v.GetInt32() == 1) return stored;
            throw new InvalidOperationException("unversioned recovery value");
        }
        var e = JsonSerializer.Deserialize<Envelope>(stored) ?? throw new InvalidOperationException("missing recovery envelope");
        if (e.Format != "apex-recovery-envelope-v1" || e.Identity != identity || !Guid.TryParseExact(e.Generation, "N", out _)
            || e.Sha256 != Digest(e.Identity, e.Generation, e.Payload))
            throw new InvalidOperationException("corrupt or foreign recovery envelope");
        using var payload = JsonDocument.Parse(e.Payload);
        return e.Payload;
    }
}
