using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace Apex.Adapters.Okx.Execution;
internal static class OkxRequestSigner
{
    internal static string Timestamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture);
    internal static string Sign(ReadOnlySpan<byte> secret,string timestamp,string method,string path,ReadOnlySpan<byte> body)
    {
        if(method is not ("GET" or "POST")) throw Grammar.Invalid();
        byte[] prefix=Encoding.UTF8.GetBytes(timestamp+method+path);
        byte[] message=new byte[checked(prefix.Length+body.Length)];
        try { prefix.CopyTo(message,0); body.CopyTo(message.AsSpan(prefix.Length)); return Convert.ToBase64String(HMACSHA256.HashData(secret,message)); }
        finally { CryptographicOperations.ZeroMemory(prefix); CryptographicOperations.ZeroMemory(message); }
    }
}
