using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Apex.Adapters.Okx;
public interface IOkxDemoSecretProvider { ValueTask<OkxDemoCredentialLease> OpenAsync(CancellationToken cancellationToken); }
[JsonConverter(typeof(OkxCredentialRedactionConverter))]
public sealed class OkxDemoCredentialLease : IDisposable
{
    private byte[] secret;
    private string? key;
    private string? pass;
    public string BindingId { get; }
    private OkxDemoCredentialLease(string bindingId, string apiKey, ReadOnlyMemory<byte> secretUtf8, string passphrase)
    { BindingId = bindingId; key = apiKey; secret = secretUtf8.ToArray(); pass = passphrase; }
    public static OkxDemoCredentialLease FromSecretStore(string bindingId, string apiKey, ReadOnlyMemory<byte> secretUtf8, string passphrase)
    {
        if (!Safe(bindingId) || !Safe(apiKey) || !Safe(passphrase) || secretUtf8.IsEmpty) throw new ArgumentException("InvalidCredentialMaterial");
        return new(bindingId, apiKey, secretUtf8, passphrase);
    }
    private static bool Safe(string value) => !string.IsNullOrEmpty(value) && !value.Any(char.IsControl);
    internal void Authenticate(HttpRequestMessage request, string stamp, string path)
    {
        ObjectDisposedException.ThrowIf(key is null, this);
        request.Headers.Add("OK-ACCESS-KEY", key);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", pass);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", stamp);
        request.Headers.Add("OK-ACCESS-SIGN", OkxGetSigner.Sign(secret, stamp, path));
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(secret); secret = []; key = null; pass = null; }
    public override string ToString() => "OKX credential lease [REDACTED]";
}
// No reflective traversal of private key material, even if a serializer is configured to include fields.
internal sealed class OkxCredentialRedactionConverter : JsonConverter<OkxDemoCredentialLease>
{
    public override OkxDemoCredentialLease Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("CredentialDeserializationDenied");
    public override void Write(Utf8JsonWriter writer, OkxDemoCredentialLease value, JsonSerializerOptions options) => writer.WriteStringValue("[REDACTED]");
}
