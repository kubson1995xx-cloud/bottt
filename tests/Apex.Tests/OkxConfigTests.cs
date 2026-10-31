using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Tests;

[Collection("OKX composition")]
public sealed class OkxConfigTests
{
    public static IEnumerable<object[]> InvalidSections()
    {
        string valid = OkxCompositionFixture.ValidConfig(NavalEnvironment.OkxDemo);
        yield return new object[] { "unknown-schema", valid.Replace("okx-naval-config-v1", "unknown") };
        foreach (string value in new[] { "null", "[]", "1", "true", "\"text\"", "{}" })
            yield return new object[] { "section-" + value, value };
        yield return new object[] { "duplicate-schema", valid.Replace("\"Schema\":", "\"Schema\":\"okx-naval-config-v1\",\"Schema\":") };
        yield return new object[] { "case-schema", valid.Replace("\"Schema\"", "\"schema\"") };
        yield return new object[] { "case-duplicate", valid.Replace("\"Schema\":", "\"schema\":\"okx-naval-config-v1\",\"Schema\":") };
        foreach (string field in new[] { "ScopeId", "Environment", "Region", "ExpectedUid", "ExpectedMainUid", "QuoteCurrency", "CredentialBindingId", "SafetyVersion" })
        {
            string scope = OkxCompositionFixture.Scope(NavalEnvironment.OkxDemo);
            using var d = System.Text.Json.JsonDocument.Parse(scope);
            string token = d.RootElement.GetProperty(field).GetRawText();
            foreach (string bad in new[] { "null", "[]", "{}", "1", "false" })
                yield return new object[] { field + "-" + bad, valid.Replace("\"" + field + "\":" + token, "\"" + field + "\":" + bad) };
            yield return new object[] { "missing-" + field, valid.Replace("\"" + field + "\":" + token, "\"Unknown\":" + token) };
            yield return new object[] { "duplicate-" + field, valid.Replace("\"" + field + "\":" + token, "\"" + field + "\":" + token + ",\"" + field + "\":" + token) };
            yield return new object[] { "case-" + field, valid.Replace("\"" + field + "\":", "\"" + field.ToLowerInvariant() + "\":") };
        }
        foreach (var pair in new[] { ("OkxDemo", "Offline"), ("OkxDemo", "live"), ("EEA", "GLOBAL"), ("123456", "12x"), ("EUR", "eur"), ("EUR", "E"), ("okx-cash-v1", "v2"), ("synthetic-demo", "../scope"), ("synthetic-demo", new string('a', 65)), ("123456", new string('1', 33)) })
            yield return new object[] { pair.Item2, valid.Replace(pair.Item1, pair.Item2) };
        yield return new object[] { "subaccount", valid.Replace("\"ExpectedMainUid\":\"123456\"", "\"ExpectedMainUid\":\"999\"") };
        foreach (string field in new[] { "Enabled", "AutoStart", "Url", "Path", "Seed", "Fixture", "TrustedSyntheticComposition", "Activation", "Password" })
            yield return new object[] { field, valid.Replace("\"ScopeId\":", "\"" + field + "\":true,\"ScopeId\":") };
        yield return new object[] { "duplicate-environment", "{\"Schema\":\"okx-naval-config-v1\",\"Scopes\":[" + OkxCompositionFixture.Scope(NavalEnvironment.OkxDemo) + "," + OkxCompositionFixture.Scope(NavalEnvironment.OkxDemo).Replace("synthetic-demo", "other") + "]}" };
        yield return new object[] { "scopes-null", "{\"Schema\":\"okx-naval-config-v1\",\"Scopes\":null}" };
    }
    [Theory, MemberData(nameof(InvalidSections))]
    public async Task MalformedSectionRefusesBeforeNormalization(string name, string section)
    {
        _ = name;
        await using var h = new OkxCompositionFixture();
        string raw = h.Raw(section).Replace("SYNTHETIC-PAIR", ""); // detect forbidden legacy normalization
        File.WriteAllText(h.ConfigPath, raw);
        File.WriteAllText(Path.Combine(h.Root, "preserved.bin"), "private synthetic preexisting bytes");
        var before = h.Inventory();
        int exit = await h.StartHostAsync(raw);
        Assert.Equal(2, exit); // RED: baseline reaches HTTP and exits 0, not a timeout/setup failure.
        Assert.False(h.ReachedHttp);
        Assert.Empty(h.Resources);
        Assert.Equal(before, h.Inventory());
    }
    [Theory]
    [InlineData("duplicate")][InlineData("case")]
    public async Task TopLevelDuplicatesAndCaseRefuse(string kind)
    {
        await using var h = new OkxCompositionFixture();
        string valid = OkxCompositionFixture.ValidConfig(NavalEnvironment.OkxDemo);
        string raw = kind == "case" ? h.Raw(valid, "okxnaval") : h.Raw(valid)[..^1] + ",\"OkxNaval\":" + valid + "}";
        File.WriteAllText(h.ConfigPath, raw); var before = h.Inventory();
        Assert.Equal(2, await h.StartHostAsync(raw)); Assert.False(h.ReachedHttp); Assert.Equal(before, h.Inventory());
    }
    [Theory]
    [InlineData("omitted")][InlineData("empty")][InlineData("demo")][InlineData("live")]
    public async Task ValidHostControlsReachActualHttp(string kind)
    {
        await using var h = new OkxCompositionFixture();
        string? section = kind switch { "omitted" => null, "empty" => "{\"Schema\":\"okx-naval-config-v1\",\"Scopes\":[]}", "demo" => OkxCompositionFixture.ValidConfig(NavalEnvironment.OkxDemo), _ => OkxCompositionFixture.ValidConfig(NavalEnvironment.Live) };
        string raw = h.Raw(section); File.WriteAllText(h.ConfigPath, raw); var before = h.Inventory();
        Assert.Equal(0, await h.StartHostAsync(raw)); Assert.True(h.ReachedHttp); Assert.Equal(before, h.Inventory());
        Assert.False(Directory.Exists(Path.Combine(h.Root, "okx")));
    }
}
