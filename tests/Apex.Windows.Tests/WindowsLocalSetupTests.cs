using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Apex.Desktop.Naval;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Windows.Tests;

// Task2 parser prerequisite only: no first-run provisioning, auth or production host seam.
public sealed class WindowsLocalSetupTests
{
    private static (string Directory, byte[] Bytes, NavalReadScope Scope) Presentation()
    {
        Assert.True(OperatingSystem.IsWindows(), "Native Windows Source gate required; Linux is not acceptance.");
        string directory = Environment.GetEnvironmentVariable("APEX_PRESENTATION_TEST_DIRECTORY") ?? throw new InvalidOperationException("Generate with New-ApexPresentation and set APEX_PRESENTATION_TEST_DIRECTORY.");
        Assert.Equal(new[] { "naval-publication.json", "presentation.json" }, Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray());
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "presentation.json")));
        var m = metadata.RootElement;
        Assert.Equal("apex-presentation-v1", m.GetProperty("schema").GetString());
        var options = JsonSerializer.Deserialize<NavalReadScopeOptions>(m.GetProperty("scope").GetRawText(), new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(null, false) }
        })!;
        Assert.Equal("presentation", options.Id);
        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, "naval-publication.json"));
        Assert.Equal(m.GetProperty("publicationSha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var export = JsonDocument.Parse(bytes);
        foreach (string name in new[] { "inputSha256", "coreSha256", "backtesterSha256" })
            Assert.Equal(m.GetProperty(name).GetString(), export.RootElement.GetProperty("provenance").GetProperty(name).GetString());
        return (directory, bytes, new NavalReadScope(options.Id!, options.PublicationScopeId!, options.PortfolioId!, options.ConfigDigest!, options.Accounts!));
    }

    [Fact, Trait("Gate", "Source")]
    public async Task PresentationIsGenuineHistoricalExport()
    {
        var (directory, bytes, scope) = Presentation();
        var e = NavalPublicationReader.Parse(bytes, scope);
        var c = e.Publication.Journal.Checkpoint;
        Assert.Equal(scope.PublicationScopeId, e.Publication.Journal.ScopeId);
        Assert.Equal(scope.PortfolioId, c.PortfolioId);
        Assert.Equal(scope.ConfigDigest, c.ConfigDigest);
        Assert.Equal(scope.Accounts, c.Accounts.Select(a => a.Account).ToArray());
        Assert.NotEmpty(e.Publication.Journal.Batches);
        Assert.Equal("HISTORICAL_OFFLINE_CHECKPOINT", e.TemporalClassification);
        Assert.Equal("UNKNOWN", e.SourceFreshness);
        Assert.Equal("NOT_CONFIGURED", c.BrokerState);
        Assert.Null(c.BrokerAccountId); Assert.Null(c.BrokerEquity);
        Assert.Null(c.RealizedPnl); Assert.Null(c.UnrealizedPnl);
        foreach (var a in scope.Accounts) { Assert.Equal("SYNTHETIC", a.Venue); Assert.Equal("Offline", a.Environment.ToString()); }
        foreach (string forbidden in new[] { "receipt", "credential", "password", "apiKey", "accessToken", "privateKey", "executionAuthority" })
            Assert.DoesNotContain("\"" + forbidden + "\"", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);

        // Build output is public input, not a runtime private-data location. Runner TEMP
        // can have replace-child ancestors: exercise the passive service on an identical
        // copy under the existing Task1 guard, without provisioning a profile or host.
        string privateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexPresentationTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var lease = WindowsPrivateData.Open(privateDirectory, AppContext.BaseDirectory);
            string publication = Path.Combine(lease.Path, "naval-publication.json");
            File.WriteAllBytes(publication, bytes);
            var reader = new NavalPublicationReader(publication, scope);
            var service = new NavalReadService(reader, scope);
            var first = await service.SnapshotAsync(scope, e.SourceClockUtc.AddDays(1), null, default);
            var later = await service.SnapshotAsync(scope, e.SourceClockUtc.AddYears(10), null, default);
            var restarted = await new NavalReadService(reader, scope).SnapshotAsync(scope, e.SourceClockUtc.AddYears(11), null, default);
            foreach (var result in new[] { first, later, restarted })
            {
                Assert.True(result.StatusCode == 200, result.Body.ReasonCode);
                Assert.Equal("AVAILABLE", result.Body.Availability);
                Assert.Equal("NOT_CONFIGURED", result.Body.Broker.State);
                Assert.Equal("UNKNOWN", result.Body.Broker.RuntimeReadiness);
                Assert.Equal("UNKNOWN", result.Body.Broker.Coverage);
                Assert.Null(result.Body.Broker.RealizedPnl); Assert.Null(result.Body.Broker.UnrealizedPnl);
                Assert.All(result.Body.Sources, s => { Assert.True(s.Stale); Assert.Equal("UNKNOWN", s.Quality); Assert.Equal("UNKNOWN", s.Coverage); Assert.NotEqual("LIVE", s.Freshness); });
                Assert.Equal(JsonSerializer.Serialize(c, NavalPublicationReader.Json), JsonSerializer.Serialize(result.Body.Checkpoint, NavalPublicationReader.Json));
                Assert.Equal(e.Publication.FinancialRevision, result.Body.SnapshotVersion);
                Assert.Equal(e.Publication.AsOfSequence, result.Body.AsOfSequence);
            }
            Assert.Equal(bytes, File.ReadAllBytes(publication));
        }
        finally { if (Directory.Exists(privateDirectory)) Directory.Delete(privateDirectory, true); }
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, "naval-publication.json")));
    }

    [Theory, Trait("Gate", "Source")]
    [InlineData("corrupt-rehashed-export")]
    [InlineData("foreign-scope")]
    [InlineData("live-label")]
    [InlineData("credential")]
    public void PresentationTypedParserRefusesMutations(string mutation)
    {
        var (_, bytes, scope) = Presentation();
        var n = JsonNode.Parse(bytes)!;
        if (mutation == "foreign-scope")
        {
            scope = new NavalReadScope("presentation", new string('0', 64), scope.PortfolioId, scope.ConfigDigest, scope.Accounts);
        }
        else
        {
            if (mutation == "corrupt-rehashed-export") n["publication"]!["journal"]!["checkpoint"]!["currentQuality"] = "LIVE";
            if (mutation == "live-label") n["temporalClassification"] = "LIVE";
            if (mutation == "credential") n["credential"] = "NOT_A_SECRET";
            // Rehash the outer content using the real typed canonical serializer. A matching
            // outer SHA cannot make invalid getter-only labels or unknown fields authoritative.
            var typed = JsonSerializer.Deserialize<NavalPublicationEnvelope>(bytes, NavalPublicationReader.Json)!;
            if (mutation == "live-label") typed = typed with { TemporalClassification = "LIVE" };
            var content = new NavalPublicationContent(typed.Schema, typed.TemporalClassification, typed.SourceClockUtc, typed.SourceFreshness, typed.Provenance, typed.Publication);
            n["contentSha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, NavalPublicationReader.Json))).ToLowerInvariant();
            bytes = JsonSerializer.SerializeToUtf8Bytes(n);
        }
        Assert.ThrowsAny<Exception>(() => NavalPublicationReader.Parse(bytes, scope));
    }

    [Fact, Trait("Gate", "Source")]
    public void PresentationBuildScratchIsPrivateAndAuthorityIsNotPublic()
    {
        var (directory, _, _) = Presentation();
        string scratch = Path.GetDirectoryName(directory)!;
        Assert.True(File.Exists(Path.Combine(scratch, "presentation-result.json")));
        Assert.True(Directory.Exists(Path.Combine(scratch, "presentation-authority")));
        var trusted = new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" };
        foreach (string path in new[] { scratch, directory, Path.Combine(scratch, "presentation-authority") })
        {
            var acl = new DirectoryInfo(path).GetAccessControl();
            Assert.Equal(WindowsIdentity.GetCurrent().User, acl.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(acl.AreAccessRulesProtected);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            { Assert.Contains(rule.IdentityReference.Value, trusted); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); }
        }
    }
}
