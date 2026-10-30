using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Microsoft.AspNetCore.Http;

namespace Apex.Desktop.Naval;

// Configuration is data, never a principal. The runtime copies and validates it once.
public sealed class NavalReadOptions
{
    public bool Enabled { get; set; }
    public string? PublicationPath { get; set; }
    public string? CredentialFilePath { get; set; }
    public NavalReadScopeOptions? Scope { get; set; }
}
public sealed class NavalReadScopeOptions
{
    public string? Id { get; set; }
    public string? PublicationScopeId { get; set; }
    public NavalPortfolioId? PortfolioId { get; set; }
    public string? ConfigDigest { get; set; }
    public NavalAccountId[]? Accounts { get; set; }
}
internal sealed record NavalReadScope
{
    public string Id { get; }
    public string PublicationScopeId { get; }
    public NavalPortfolioId PortfolioId { get; }
    public string ConfigDigest { get; }
    public IReadOnlyList<NavalAccountId> Accounts { get; }
    internal NavalReadScope(string id, string publicationScopeId, NavalPortfolioId portfolioId, string configDigest, IReadOnlyList<NavalAccountId> accounts)
    { Id = id; PublicationScopeId = publicationScopeId; PortfolioId = portfolioId; ConfigDigest = configDigest; Accounts = Array.AsReadOnly(accounts.ToArray()); }
}
internal enum NavalReadAccess { Denied, ReadOnly, Operator }
internal interface INavalAccessPolicy { NavalReadAccess Authorize(HttpContext context, NavalReadScope scope); }
internal sealed record NavalRuntimeOptions(string? PublicationPath, NavalReadScope Scope, NavalAccessPolicy Access);
internal sealed class NavalAccessException : Exception
{
    internal string Code { get; }
    internal NavalAccessException(string code) : base(code) { Code = code; }
}
internal sealed class NavalAccessPolicy : INavalAccessPolicy
{
    private sealed record Principal(string Id, NavalReadAccess Access, byte[] Digest, string[] Grants);
    private readonly Principal[] principals;
    private readonly object verifiedKey = new();
    private NavalAccessPolicy(Principal[] principals) { this.principals = principals; }
    internal static bool Identifier(string? s) => s is { Length: >= 1 and <= 64 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    internal static bool Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
    internal static void ValidateRawConfig(string json)
    {
        // Keep legacy malformed-JSON/null-root exceptions on ConfigStore's established path.
        // Only the new section introduces Naval-specific validation failures.
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        try
        {
            var sections = doc.RootElement.EnumerateObject().Where(p => p.Name.Equals("NavalRead", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sections.Length == 0) return;
            if (sections.Length != 1 || sections[0].Name != "NavalRead") throw new NavalAccessException("NAVAL_CONFIG_INVALID");
            var e = sections[0].Value;
            Fields(e, new[] { "Enabled" }, new[] { "Enabled", "PublicationPath", "CredentialFilePath", "Scope" });
            if (e.GetProperty("Enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new NavalAccessException("NAVAL_CONFIG_INVALID");
            if (e.TryGetProperty("Scope", out var scope) && scope.ValueKind != JsonValueKind.Null)
            {
                Fields(scope, new[] { "Id", "PublicationScopeId", "PortfolioId", "ConfigDigest", "Accounts" });
                Fields(scope.GetProperty("PortfolioId"), new[] { "PortfolioId", "ReportingCurrency" });
                foreach (var a in scope.GetProperty("Accounts").EnumerateArray()) Fields(a, new[] { "Venue", "Environment", "AccountId", "QuoteCurrency" });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException && e is not NavalAccessException) { throw new NavalAccessException("NAVAL_CONFIG_INVALID"); }
    }
    internal static NavalRuntimeOptions? Load(NavalReadOptions? options)
    {
        if (options?.Enabled != true) return null;
        try
        {
            var s = options.Scope;
            if (s == null || !Identifier(s.Id) || !Hex(s.PublicationScopeId) || !Hex(s.ConfigDigest) || s.PortfolioId == null ||
                !Text(s.PortfolioId.PortfolioId) || !Text(s.PortfolioId.ReportingCurrency) || s.Accounts is not { Length: > 0 } ||
                s.Accounts.Any(a => a == null || !Text(a.Venue) || !Text(a.AccountId) || !Text(a.QuoteCurrency) || !Enum.IsDefined(a.Environment)) || s.Accounts.Distinct().Count() != s.Accounts.Length)
                throw new NavalAccessException("NAVAL_CONFIG_INVALID");
            var scope = new NavalReadScope(s.Id!, s.PublicationScopeId!, s.PortfolioId, s.ConfigDigest!, s.Accounts);
            if (options.CredentialFilePath == null) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID");
            NavalPrivatePath.Check(options.CredentialFilePath, true, true);
            if (options.PublicationPath != null)
            {
                NavalPrivatePath.Check(options.PublicationPath, false, false, false);
                if (Path.GetFullPath(options.PublicationPath).Equals(Path.GetFullPath(options.CredentialFilePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new NavalAccessException("NAVAL_PATH_INVALID");
            }
            using var stream = new FileStream(options.CredentialFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 65536) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID");
            using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            { if (memory.Length + count > 65536) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID"); memory.Write(buffer, 0, count); }
            using var d = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }); var root = d.RootElement;
            Fields(root, new[] { "schema", "principals" });
            if (root.GetProperty("schema").GetString() != "naval-local-access-v1") throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID");
            var list = root.GetProperty("principals"); if (list.GetArrayLength() is < 1 or > 16) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID");
            var ps = new List<Principal>(); var ids = new HashSet<string>(StringComparer.Ordinal); var digests = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in list.EnumerateArray())
            {
                Fields(p, new[] { "id", "role", "secretSha256", "allowedScopeIds" });
                string id = p.GetProperty("id").GetString()!, role = p.GetProperty("role").GetString()!, hash = p.GetProperty("secretSha256").GetString()!;
                var grants = p.GetProperty("allowedScopeIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
                if (!Identifier(id) || !ids.Add(id) || role is not ("ReadOnly" or "Operator") || !Hex(hash) || hash.Any(c => c is >= 'A' and <= 'F') || !digests.Add(hash) || grants.Length != 1 || grants[0] != scope.Id) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID");
                ps.Add(new(id, role == "Operator" ? NavalReadAccess.Operator : NavalReadAccess.ReadOnly, Convert.FromHexString(hash), grants));
            }
            if (!ps.Any(p => p.Access == NavalReadAccess.Operator) || !ps.Any(p => p.Access == NavalReadAccess.ReadOnly)) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID");
            return new(options.PublicationPath, scope, new(ps.ToArray()));
        }
        catch (NavalAccessException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID"); }
    }
    private static bool Text(string? s) => s is { Length: >= 1 and <= 256 } && !s.Any(char.IsControl);
    internal static void Fields(JsonElement e, string[] required, string[]? allowed = null)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new NavalAccessException("NAVAL_CONFIG_INVALID");
        var seen = new HashSet<string>(StringComparer.Ordinal); var permitted = (allowed ?? required).ToHashSet(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) if (!permitted.Contains(p.Name) || !seen.Add(p.Name)) throw new NavalAccessException("NAVAL_CONFIG_INVALID");
        if (required.Any(n => !seen.Contains(n))) throw new NavalAccessException("NAVAL_CONFIG_INVALID");
    }
    public NavalReadAccess Authorize(HttpContext context, NavalReadScope scope) => context.Items.TryGetValue(verifiedKey, out var p) && p is Principal principal && principal.Grants.Contains(scope.Id, StringComparer.Ordinal) ? principal.Access : NavalReadAccess.Denied;
    internal async Task<bool> Enter(HttpContext ctx)
    {
        if (ctx.Request.Path.Value == "/bridge")
        { if (ctx.Request.Headers.ContainsKey("Authorization") || ctx.Request.Headers.ContainsKey("Origin")) { await Error(ctx, 403); return false; } return true; }
        var p = Authenticate(ctx);
        string? path = ctx.Request.Path.Value;
        bool naval = path == "/api/naval/snapshot" || path == "/api/naval/events";
        if (p == null)
        {
            ctx.Response.Headers["WWW-Authenticate"] = naval ? "Bearer" : "Basic realm=\"APEX Local Operator\", charset=\"UTF-8\"";
            await Error(ctx, 401); return false;
        }
        if (p.Access == NavalReadAccess.ReadOnly && (!naval || !HttpMethods.IsGet(ctx.Request.Method))) { await Error(ctx, 403); return false; }
        ctx.Items[verifiedKey] = p;
        if (p.Access == NavalReadAccess.Operator && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
        {
            var origin = ctx.Request.Headers["Origin"];
            string expected = ctx.Request.Scheme + "://" + ctx.Request.Host.Value;
            if ((origin.Count != 0 && (origin.Count != 1 || origin[0] != expected)) || ctx.Request.Headers["Sec-Fetch-Site"].Any(v => v == "cross-site")) { await Error(ctx, 403); return false; }
        }
        if (ctx.Request.Path.StartsWithSegments("/api/naval"))
        {
            if (!naval) { await Error(ctx, 404); return false; }
            if (!HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.Headers["Allow"] = "GET"; await Error(ctx, 405); return false; }
        }
        return true;
    }
    private Principal? Authenticate(HttpContext ctx)
    {
        if (!string.IsNullOrEmpty(ctx.Request.Headers["X-Apex-Token"].ToString())) return null;
        var a = ctx.Request.Headers["Authorization"]; if (a.Count != 1) return null;
        string? h = a[0]; if (h == null || h.Length > 4096 || h.Any(c => c > 127 || char.IsControl(c)) || h.Contains(',')) return null;
        string? id = null, secret = null; bool basic = false;
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) secret = h[7..];
        else if (h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            basic = true;
            try { var bytes = Convert.FromBase64String(h[6..]); if (Convert.ToBase64String(bytes) != h[6..] || bytes.Any(b => b > 127)) return null; string pair = Encoding.ASCII.GetString(bytes); int split = pair.IndexOf(':'); if (split < 1) return null; id = pair[..split]; secret = pair[(split + 1)..]; } catch (FormatException) { return null; }
        }
        if (secret is not { Length: 43 } || secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) return null;
        try { var raw = Convert.FromBase64String(secret.Replace('-', '+').Replace('_', '/') + "="); if (raw.Length != 32 || Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_') != secret) return null; } catch (FormatException) { return null; }
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(secret)); Principal? match = null;
        foreach (var p in principals) { bool equal = CryptographicOperations.FixedTimeEquals(digest, p.Digest); if (equal && (!basic || (p.Id == id && p.Access == NavalReadAccess.Operator)) && (basic || p.Access == NavalReadAccess.ReadOnly)) match = p; }
        return match;
    }
    internal static async Task Error(HttpContext ctx, int status)
    {
        ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json";
        string reason = status switch { 401 => "AUTH_REQUIRED", 404 => "NOT_FOUND", 405 => "METHOD_NOT_ALLOWED", _ => "ACCESS_DENIED" };
        await ctx.Response.WriteAsync("{\"reasonCode\":\"" + reason + "\"}");
    }
}

internal static class NavalPrivatePath
{
    internal static void Check(string path, bool credential, bool requireLeaf, bool checkLeafPermissions = true)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains('%') || path.Contains('*') || path.Contains('?') || path.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new NavalAccessException("NAVAL_PATH_INVALID");
            string full = Path.GetFullPath(path); if (full != path || Path.GetFileName(full).Length == 0) throw new NavalAccessException("NAVAL_PATH_INVALID");
            string baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (full.StartsWith(baseDir + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new NavalAccessException("NAVAL_PATH_INVALID");
            var chain = new List<string>(); for (var d = new DirectoryInfo(Path.GetDirectoryName(full)!); d != null; d = d.Parent) chain.Add(d.FullName); chain.Reverse();
            bool privateTraversal = false;
            foreach (string d in chain)
            {
                if (!Directory.Exists(d) || (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0 || File.Exists(Path.Combine(d, ".naval-owner.lock")) || File.Exists(Path.Combine(d, ".naval-initialized")) || Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, "APEX.Trading.Demo.sln"))) throw new NavalAccessException("NAVAL_PATH_INVALID");
                if (OperatingSystem.IsWindows()) CheckWindows(new DirectoryInfo(d), d == Path.GetDirectoryName(full));
                else
                {
                    var m = File.GetUnixFileMode(d);
                    if (!privateTraversal && (m & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID");
                    if ((m & (UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) == 0) privateTraversal = true;
                }
            }
            string parent = Path.GetDirectoryName(full)!;
            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(parent) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID");
            if (!File.Exists(full)) { if (requireLeaf) throw new NavalAccessException("NAVAL_CREDENTIAL_INVALID"); return; }
            if ((File.GetAttributes(full) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new NavalAccessException("NAVAL_PATH_INVALID");
            if (!checkLeafPermissions) return;
            if (OperatingSystem.IsWindows()) CheckWindows(new FileInfo(full), true);
            else
            {
                var m = File.GetUnixFileMode(full); var allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if ((credential && m != allowed) || (!credential && ((m & ~allowed) != 0 || (m & UnixFileMode.UserRead) == 0))) throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID");
            }
        }
        catch (NavalAccessException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID"); }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckWindows(FileSystemInfo info, bool leaf)
    {
        FileSystemSecurity acl = info is DirectoryInfo directory ? directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner) : ((FileInfo)info).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var descriptor = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl == null) throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID");
        var me = WindowsIdentity.GetCurrent().User!; var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null); var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        bool Trusted(IdentityReference id) => id.Equals(me) || id.Equals(system) || id.Equals(admins);
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (owner == null || !Trusted(owner)) throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID");
        // Ancestors may be traversable/readable, but no untrusted modification rights. Private leaf/parent have no broad grants.
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || Trusted(rule.IdentityReference)) continue;
            var modification = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            bool privateTarget = leaf;
            if (privateTarget || (rule.FileSystemRights & modification) != 0) throw new NavalAccessException("NAVAL_PERMISSIONS_INVALID");
        }
    }
}
