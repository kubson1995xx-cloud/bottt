using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Trusted caller adapts its private scoped store; this contract grants neither activation nor account readiness.
/// Task 1 fingerprint-use registry must be checked by that composition before opening a private session.</summary>
public interface IOkxSigningMaterialProvider { ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken cancellation); }
[JsonConverter(typeof(SigningMaterialJsonRefusal))]
public sealed class OkxSigningMaterial : IDisposable
{
    internal readonly byte[] Key, Secret, Passphrase;
    private readonly object sync=new();
    private bool disposed;
    public OkxExecutionScope Scope { get; }
    public string Provenance { get; }
    public string Fingerprint { get; }
    public OkxSigningMaterial(OkxExecutionScope scope,string provenance,ReadOnlyMemory<byte> key,ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase)
    {
        // Copies are owned here; source store retains responsibility for its originals.
        Key=key.ToArray(); Secret=secret.ToArray(); Passphrase=passphrase.ToArray();
        try
        {
            ArgumentNullException.ThrowIfNull(scope);
            if(provenance!=(scope.Environment=="Demo"?"DemoTradingApi":"LiveAccountApi") || !Header(Key) || !Header(Passphrase) || Secret.Length is <=0 or >4096) throw new ArgumentException();
            Scope=scope; Provenance=provenance; Fingerprint=Convert.ToHexString(SHA256.HashData(Key)).ToLowerInvariant();
        }
        catch { Dispose(); throw new ArgumentException("InvalidSigningMaterial"); }
    }
    private static bool Header(byte[] b)=>b.Length is >0 and <=4096 && b.All(v=>v is >=33 and <=126);
    internal void Authenticate(HttpRequestMessage request,string stamp,OkxPreparedRequest prepared)
    {
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            request.Headers.Add("OK-ACCESS-KEY",Encoding.ASCII.GetString(Key));
            request.Headers.Add("OK-ACCESS-PASSPHRASE",Encoding.ASCII.GetString(Passphrase));
            request.Headers.Add("OK-ACCESS-TIMESTAMP",stamp);
            request.Headers.Add("OK-ACCESS-SIGN",OkxRequestSigner.Sign(Secret,stamp,prepared.Method,prepared.Path,prepared.Body));
            // HTTP header strings and framework-internal copies cannot be reliably erased by this library.
        }
    }
    // Additive read-only signer. Accepted mutation Authenticate above is unchanged.
    internal void AuthenticateRead(HttpRequestMessage request,string timestamp,string exactPathAndQuery)
    {
        if(request.Method!=HttpMethod.Get || request.Content is not null || request.RequestUri?.PathAndQuery!=exactPathAndQuery)
            throw new ArgumentException("InvalidObservationRequest");
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            request.Headers.Add("OK-ACCESS-KEY",Encoding.ASCII.GetString(Key));
            request.Headers.Add("OK-ACCESS-PASSPHRASE",Encoding.ASCII.GetString(Passphrase));
            request.Headers.Add("OK-ACCESS-TIMESTAMP",timestamp);
            request.Headers.Add("OK-ACCESS-SIGN",OkxRequestSigner.Sign(Secret,timestamp,"GET",exactPathAndQuery,ReadOnlySpan<byte>.Empty));
        }
    }
    // Closed WS login only. Destination is caller-owned and must survive SendAsync completion.
    // No private immutable JSON strings, growing writer, pool or escaping scratch is used.
    internal int WriteObservationLogin(Span<byte> destination,string seconds)
    {
        Span<byte> preimage=stackalloc byte[64];Span<byte> hash=stackalloc byte[32];Span<byte> signature=stackalloc byte[44];
        try
        {
            if(!Grammar.Match(seconds,"[0-9]{1,12}"))throw new ArgumentException("InvalidLoginTimestamp");
            lock(sync)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                int n=Encoding.ASCII.GetBytes(seconds,preimage);"GET/users/self/verify"u8.CopyTo(preimage[n..]);n+="GET/users/self/verify"u8.Length;
                HMACSHA256.HashData(Secret,preimage[..n],hash);
                if(System.Buffers.Text.Base64.EncodeToUtf8(hash,signature,out _,out var written)!=System.Buffers.OperationStatus.Done||written!=44)throw new ArgumentException("LoginEncoding");
                var writer=new LoginWriter(destination);
                writer.Raw("{\"op\":\"login\",\"args\":[{\"apiKey\":"u8);writer.String(Key);
                writer.Raw(",\"passphrase\":"u8);writer.String(Passphrase);
                writer.Raw(",\"timestamp\":\""u8);writer.Raw(preimage[..seconds.Length]);
                writer.Raw("\",\"sign\":\""u8);writer.Raw(signature);writer.Raw("\"}]}"u8);
                return writer.Position;
            }
        }
        catch {CryptographicOperations.ZeroMemory(destination);throw;}
        finally {CryptographicOperations.ZeroMemory(preimage);CryptographicOperations.ZeroMemory(hash);CryptographicOperations.ZeroMemory(signature);}
    }
    private ref struct LoginWriter
    {
        private readonly Span<byte> buffer;
        internal LoginWriter(Span<byte> buffer){this.buffer=buffer;Position=0;}
        internal int Position;
        internal void Raw(scoped ReadOnlySpan<byte> value)
        {if(value.Length>buffer.Length-Position)throw new ArgumentException("LoginCapacity");value.CopyTo(buffer[Position..]);Position+=value.Length;}
        internal void String(ReadOnlySpan<byte> value)
        {
            Raw("\""u8);foreach(byte b in value){if(b is (byte)'"' or (byte)'\\')Raw("\\"u8);if(Position==buffer.Length)throw new ArgumentException("LoginCapacity");buffer[Position++]=b;}Raw("\""u8);
        }
    }
    public void Dispose() { lock(sync) { if(disposed)return; disposed=true; CryptographicOperations.ZeroMemory(Key); CryptographicOperations.ZeroMemory(Secret); CryptographicOperations.ZeroMemory(Passphrase); } }
    public override string ToString()=>"OKX signing material [REDACTED]";
}
internal sealed class SigningMaterialJsonRefusal : JsonConverter<OkxSigningMaterial>
{
    public override OkxSigningMaterial Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException("PrivateCapability");
    public override void Write(Utf8JsonWriter writer,OkxSigningMaterial value,JsonSerializerOptions options)=>throw new NotSupportedException("PrivateCapability");
}
