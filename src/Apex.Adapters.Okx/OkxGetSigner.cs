using System.Security.Cryptography;
using System.Text;
namespace Apex.Adapters.Okx;
internal static class OkxGetSigner
{
    internal static string Sign(ReadOnlySpan<byte> secretUtf8, string timestampUtc, string exactPathAndQuery)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(timestampUtc + "GET" + exactPathAndQuery);
        try { return Convert.ToBase64String(HMACSHA256.HashData(secretUtf8, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
