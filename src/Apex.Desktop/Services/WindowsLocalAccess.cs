using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Desktop.Naval;

namespace Apex.Desktop.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsLocalAccess
{
    internal sealed record Credentials(string OperatorSecret, string ReaderSecret);
    internal static void Create(string dataDirectory, string scopeId) => Create(dataDirectory, scopeId, null);
    internal static void Create(string dataDirectory, string scopeId, Action<string>? afterWrite)
    {
        if (scopeId != "presentation") throw WindowsLocalSetup.Incomplete();
        string directory = Path.Combine(dataDirectory, "private");
        // Never overwrite or rotate even half of an existing credential pair.
        if (Directory.Exists(directory) || File.Exists(directory)) throw WindowsLocalSetup.Incomplete();
        WindowsPathSecurity.CreatePrivateDirectory(directory);
        string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string op = NewSecret(), reader = NewSecret();
        if (op == reader) throw WindowsLocalSetup.Incomplete();
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new { schema = "apex-local-access-v1", scopeId, operatorSecret = op, readerSecret = reader });
        byte[] encrypted;
        try { encrypted = Crypt(plaintext, true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        AtomicFile.Write(Path.Combine(directory, "local-access.json"), JsonSerializer.Serialize(new
        {
            schema = "naval-local-access-v1", principals = new[]
            {
                new { id = "operator", role = "Operator", secretSha256 = Hash(op), allowedScopeIds = new[] { scopeId } },
                new { id = "reader", role = "ReadOnly", secretSha256 = Hash(reader), allowedScopeIds = new[] { scopeId } }
            }
        })); afterWrite?.Invoke("access");
        AtomicFile.Write(Path.Combine(directory, "local-access.dpapi"), encrypted); afterWrite?.Invoke("dpapi");
    }
    internal static bool Show(string dataDirectory) => Show(dataDirectory, WindowsLocalAccessPrompt.Show);
    internal static bool Show(string dataDirectory, Func<string, string, bool> prompt)
    {
        // Display is never provisioning. Validate all Ready state before decrypt/display.
        WindowsLocalSetup.ValidateReady(dataDirectory);
        var credentials = Load(dataDirectory, "presentation");
        return prompt("operator", credentials.OperatorSecret);
    }
    internal static Credentials Load(string dataDirectory, string scopeId)
    {
        byte[]? plaintext = null;
        try
        {
            plaintext = Crypt(WindowsLocalSetup.ReadPrivate(Path.Combine(dataDirectory, "private", "local-access.dpapi"), 65536), false);
            using var d = JsonDocument.Parse(plaintext); var root = d.RootElement;
            NavalAccessPolicy.Fields(root, new[] { "schema", "scopeId", "operatorSecret", "readerSecret" });
            if (root.GetProperty("schema").GetString() != "apex-local-access-v1" || root.GetProperty("scopeId").GetString() != scopeId || scopeId != "presentation") throw WindowsLocalSetup.Incomplete();
            string op = root.GetProperty("operatorSecret").GetString()!, reader = root.GetProperty("readerSecret").GetString()!;
            if (!Secret(op) || !Secret(reader) || op == reader) throw WindowsLocalSetup.Incomplete();
            using var map = JsonDocument.Parse(WindowsLocalSetup.ReadPrivate(Path.Combine(dataDirectory, "private", "local-access.json"), 65536));
            NavalAccessPolicy.Fields(map.RootElement, new[] { "schema", "principals" });
            if (map.RootElement.GetProperty("schema").GetString() != "naval-local-access-v1") throw WindowsLocalSetup.Incomplete();
            var principals = map.RootElement.GetProperty("principals"); if (principals.GetArrayLength() != 2) throw WindowsLocalSetup.Incomplete();
            foreach (var (id, role, secret) in new[] { ("operator", "Operator", op), ("reader", "ReadOnly", reader) })
            {
                var matches = principals.EnumerateArray().Where(p => p.GetProperty("id").GetString() == id).ToArray();
                if (matches.Length != 1) throw WindowsLocalSetup.Incomplete(); var p = matches[0];
                NavalAccessPolicy.Fields(p, new[] { "id", "role", "secretSha256", "allowedScopeIds" });
                if (p.GetProperty("role").GetString() != role || p.GetProperty("secretSha256").GetString() != Hash(secret) || !p.GetProperty("allowedScopeIds").EnumerateArray().Select(s => s.GetString()).SequenceEqual(new[] { scopeId })) throw WindowsLocalSetup.Incomplete();
            }
            return new(op, reader);
        }
        catch (Exception e) when (e is not OutOfMemoryException && e is not WindowsDataException) { throw WindowsLocalSetup.Incomplete(); }
        finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static string Hash(string secret) => WindowsLocalSetup.Digest(Encoding.ASCII.GetBytes(secret));
    private static bool Secret(string? s)
    {
        if (s is not { Length: 43 } || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) return false;
        byte[] raw = Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + "=");
        try { return raw.Length == 32 && Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_') == s; }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { internal int Length; internal IntPtr Data; }
    // flags=CRYPTPROTECT_UI_FORBIDDEN only. Never CRYPTPROTECT_LOCAL_MACHINE.
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr data);
    private static byte[] Crypt(byte[] bytes, bool protect)
    {
        byte[] purpose = Encoding.UTF8.GetBytes("apex-local-access-v1");
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var entropy = new DataBlob { Length = purpose.Length, Data = Marshal.AllocHGlobal(purpose.Length) }; DataBlob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(purpose, 0, entropy.Data, purpose.Length);
            bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok || output.Length <= 0 || output.Length > 65536) throw WindowsLocalSetup.Incomplete();
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Zero(input); Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) { Zero(output); LocalFree(output.Data); }
        }
    }
    private static void Zero(DataBlob blob) { for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blob.Data, i, 0); }
}
