using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.Core.Naval;

namespace Apex.Desktop.Okx;

public sealed class OkxNavalOptions
{
    public string? Schema { get; set; }
    public OkxScopeOptions[]? Scopes { get; set; }
}
public sealed class OkxScopeOptions
{
    public string? ScopeId { get; set; }
    public string? Environment { get; set; }
    public string? Region { get; set; }
    public string? ExpectedUid { get; set; }
    public string? ExpectedMainUid { get; set; }
    public string? QuoteCurrency { get; set; }
    public string? CredentialBindingId { get; set; }
    public string? SafetyVersion { get; set; }
}
internal sealed record OkxScopeBinding(string ScopeId, NavalAccountId Account,
    string MainUid, string CredentialBindingId, string SafetyVersion, string ConfigDigest);
internal sealed record OkxScopePaths(string Root, string Credentials, string Authority, string Publication);

internal static class OkxConfiguration
{
    private static readonly string[] ScopeFields = { "ScopeId", "Environment", "Region", "ExpectedUid", "ExpectedMainUid", "QuoteCurrency", "CredentialBindingId", "SafetyVersion" };
    internal static void ValidateRaw(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var matches = d.RootElement.EnumerateObject().Where(p => p.Name.Equals("OkxNaval", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return;
            if (matches.Length != 1 || matches[0].Name != "OkxNaval") throw Invalid();
            var section = matches[0].Value;
            Fields(section, new[] { "Schema", "Scopes" });
            if (Text(section, "Schema") != "okx-naval-config-v1") throw Invalid();
            var scopes = section.GetProperty("Scopes");
            if (scopes.ValueKind != JsonValueKind.Array || scopes.GetArrayLength() > 2) throw Invalid();
            var envs = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in scopes.EnumerateArray())
            {
                Fields(scope, ScopeFields);
                string id = Text(scope, "ScopeId"), env = Text(scope, "Environment");
                string uid = Text(scope, "ExpectedUid"), main = Text(scope, "ExpectedMainUid");
                if (!Identifier(id) || !Identifier(Text(scope, "CredentialBindingId")) ||
                    env is not ("OkxDemo" or "Live") || !envs.Add(env) || !ids.Add(id) ||
                    Text(scope, "Region") != "EEA" || !Match(uid, "[0-9]{1,32}") || uid != main ||
                    !Match(Text(scope, "QuoteCurrency"), "[A-Z0-9]{2,12}") || Text(scope, "SafetyVersion") != "okx-cash-v1") throw Invalid("OKX_SCOPE_INVALID");
            }
        }
        catch (JsonException) { throw Invalid(); }
    }
    internal static IReadOnlyList<OkxScopeBinding> Bind(OkxNavalOptions? options)
    {
        if (options == null) return Array.Empty<OkxScopeBinding>();
        ValidateRaw(JsonSerializer.Serialize(new { OkxNaval = options }));
        return Array.AsReadOnly(options.Scopes!.Select(s =>
        {
            var account = new NavalAccountId("OKX", s.Environment == "OkxDemo" ? NavalEnvironment.OkxDemo : NavalEnvironment.Live, s.ExpectedUid!, s.QuoteCurrency!);
            // Ordered canonical identity, no clocks, secrets, defaults or culture-sensitive numbers.
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { "okx-naval-config-v1", "OKX", "EEA", s.ScopeId!, s.Environment!, s.ExpectedUid!, s.ExpectedMainUid!, s.QuoteCurrency!, s.CredentialBindingId!, s.SafetyVersion! });
            return new OkxScopeBinding(s.ScopeId!, account, s.ExpectedMainUid!, s.CredentialBindingId!, s.SafetyVersion!, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }).ToArray());
    }
    internal static OkxScopePaths Paths(string admittedDataRoot, OkxScopeBinding binding)
    {
        ValidateBinding(binding);
        if (!Path.IsPathFullyQualified(admittedDataRoot) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(admittedDataRoot)) != Path.TrimEndingDirectorySeparator(admittedDataRoot)) throw Invalid("OKX_SCOPE_INVALID");
        string root = Path.Combine(admittedDataRoot, "okx", binding.Account.Environment == NavalEnvironment.OkxDemo ? "demo" : "live", binding.ScopeId);
        return new(root, Path.Combine(root, "credentials.dpapi"), Path.Combine(root, "authority"), Path.Combine(root, "publication"));
    }
    internal static void ValidateBinding(OkxScopeBinding binding)
    {
        var options = new OkxNavalOptions { Schema = "okx-naval-config-v1", Scopes = new[] { new OkxScopeOptions {
            ScopeId = binding.ScopeId, Environment = binding.Account.Environment.ToString(), Region = "EEA", ExpectedUid = binding.Account.AccountId,
            ExpectedMainUid = binding.MainUid, QuoteCurrency = binding.Account.QuoteCurrency, CredentialBindingId = binding.CredentialBindingId, SafetyVersion = binding.SafetyVersion } } };
        var expected = Bind(options)[0];
        if (binding != expected) throw Invalid("OKX_SCOPE_INVALID");
    }
    internal static void Fields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length || names.Any(n => !fields.Contains(n, StringComparer.Ordinal))) throw Invalid();
    }
    private static string Text(JsonElement value, string name)
    {
        var v = value.GetProperty(name);
        if (v.ValueKind != JsonValueKind.String) throw Invalid();
        return v.GetString()!;
    }
    internal static bool Identifier(string s) => Match(s, "[A-Za-z0-9_-]{1,64}");
    private static bool Match(string s, string pattern) => Regex.IsMatch(s, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
    private static InvalidDataException Invalid(string code = "OKX_CONFIG_INVALID") => new(code);
}
