using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Desktop.Naval;

namespace Apex.Desktop.Services;

internal enum LocalSetupState { Empty, Existing, Pending, Ready }
internal sealed record LocalSetupResult(ConfigStore Store, bool Initialized, bool NeedsCredentialPrompt);

// Called only under the admitted private-root lease and exclusive apex.lock.
// A pending marker is durable before anything that could look like a usable config.
[SupportedOSPlatform("windows")]
internal static class WindowsLocalSetup
{
    private const string Profile = "windows-local-presentation-v1";
    internal static WindowsDataException Incomplete() => new("HOST_LOCAL_SETUP_INCOMPLETE");
    private static readonly JsonSerializerOptions Strict = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(null, false) } };

    internal static LocalSetupState Classify(string dataDirectory)
    {
        string marker = Path.Combine(dataDirectory, "local-startup.json");
        if (File.Exists(marker))
        {
            try
            {
                using var d = JsonDocument.Parse(ReadPrivate(marker, 65536));
                Marker(d.RootElement);
                return d.RootElement.GetProperty("state").GetString() == "ready" ? LocalSetupState.Ready : LocalSetupState.Pending;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return LocalSetupState.Pending; }
        }
        return Directory.EnumerateFileSystemEntries(dataDirectory).Any(p => !Path.GetFileName(p).Equals("apex.lock", StringComparison.Ordinal)) ? LocalSetupState.Existing : LocalSetupState.Empty;
    }
    internal static LocalSetupResult Ensure(string dataDirectory, string presentationDirectory) => Ensure(dataDirectory, presentationDirectory, null);
    // Per-call source test observer: no global state, CLI/config/environment switch or financial authority.
    internal static LocalSetupResult Ensure(string dataDirectory, string presentationDirectory, Action<string>? afterWrite)
    {
        try
        {
            var state = Classify(dataDirectory);
            if (state == LocalSetupState.Pending) throw Incomplete();
            if (state == LocalSetupState.Existing)
            {
                var existing = ReadConfig(dataDirectory);
                _ = NavalAccessPolicy.Load(existing.NavalRead); // validate before legacy constructor can normalize anything
                return new(new ConfigStore(dataDirectory), false, false);
            }
            if (state == LocalSetupState.Ready)
            {
                ValidateReady(dataDirectory);
                return new(new ConfigStore(dataDirectory), false, false);
            }

            var (bytes, scope, hash) = ReadPresentation(presentationDirectory);
            string marker = Path.Combine(dataDirectory, "local-startup.json");
            void WriteMarker(string status) => AtomicFile.Write(marker, JsonSerializer.Serialize(new { schema = "apex-local-startup-v1", profile = Profile, state = status, publicationSha256 = hash, scope }, Strict));
            WriteMarker("pending"); afterWrite?.Invoke("pending");
            WindowsLocalAccess.Create(dataDirectory, scope.Id!, afterWrite);
            string publicationDirectory = Path.Combine(dataDirectory, "publication");
            WindowsPathSecurity.CreatePrivateDirectory(publicationDirectory);
            string publication = Path.Combine(publicationDirectory, "naval-publication.json");
            AtomicFile.Write(publication, bytes); afterWrite?.Invoke("publication");
            var initial = new HostConfig
            {
                PairingKey = ConfigStore.NewPairingKey(), ApiToken = ConfigStore.NewToken(),
                NavalRead = new NavalReadOptions { Enabled = true, CredentialFilePath = Path.Combine(dataDirectory, "private", "local-access.json"), PublicationPath = publication, Scope = scope }
            };
            initial.News.Enabled = false;
            var store = new ConfigStore(dataDirectory, initial); afterWrite?.Invoke("config");
            // Check the complete private state while still pending; no anonymous fallback.
            ValidateState(dataDirectory, scope, hash, initial);
            WriteMarker("ready"); afterWrite?.Invoke("ready");
            return new(store, true, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NavalAccessException or CryptographicException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw Incomplete(); }
    }
    internal static void ValidateReady(string dataDirectory)
    {
        try
        {
            if (Classify(dataDirectory) != LocalSetupState.Ready) throw Incomplete();
            using var d = JsonDocument.Parse(ReadPrivate(Path.Combine(dataDirectory, "local-startup.json"), 65536));
            var scope = ReadScope(d.RootElement.GetProperty("scope"));
            ValidateState(dataDirectory, scope, d.RootElement.GetProperty("publicationSha256").GetString()!, ReadConfig(dataDirectory));
        }
        catch (Exception e) when (e is not OutOfMemoryException && e is not WindowsDataException) { throw Incomplete(); }
    }
    private static void ValidateState(string root, NavalReadScopeOptions scope, string hash, HostConfig cfg)
    {
        var naval = cfg.NavalRead;
        if (naval?.Enabled != true || naval.CredentialFilePath != Path.Combine(root, "private", "local-access.json") || naval.PublicationPath != Path.Combine(root, "publication", "naval-publication.json") || JsonSerializer.Serialize(naval.Scope, Strict) != JsonSerializer.Serialize(scope, Strict)) throw Incomplete();
        var runtime = NavalAccessPolicy.Load(naval) ?? throw Incomplete();
        byte[] bytes = ReadPrivate(naval.PublicationPath, NavalPublicationReader.MaximumBytes);
        if (Digest(bytes) != hash) throw Incomplete();
        ValidateSynthetic(NavalPublicationReader.Parse(bytes, runtime.Scope), scope);
        _ = WindowsLocalAccess.Load(root, scope.Id!);
    }
    private static HostConfig ReadConfig(string root)
    {
        byte[] bytes = ReadPrivate(Path.Combine(root, "apex.config.json"), 4 * 1024 * 1024);
        string raw = System.Text.Encoding.UTF8.GetString(bytes);
        NavalAccessPolicy.ValidateRawConfig(raw);
        var cfg = JsonSerializer.Deserialize<HostConfig>(raw, ConfigStore.Json) ?? throw Incomplete();
        // No constructor repairs on malformed existing/Ready state.
        if (cfg.Risk == null || cfg.Risk.Validate() != null || cfg.News == null || cfg.Universe == null || cfg.Accounts == null || cfg.AllowedBrokers == null || string.IsNullOrWhiteSpace(cfg.PairingKey) || string.IsNullOrWhiteSpace(cfg.ApiToken) || cfg.ApiToken.Length < 32) throw Incomplete();
        return cfg;
    }
    private static void Marker(JsonElement m)
    {
        NavalAccessPolicy.Fields(m, new[] { "schema", "profile", "state", "publicationSha256", "scope" });
        if (m.GetProperty("schema").GetString() != "apex-local-startup-v1" || m.GetProperty("profile").GetString() != Profile || m.GetProperty("state").GetString() is not ("pending" or "ready") || !LowerHash(m.GetProperty("publicationSha256").GetString())) throw Incomplete();
        _ = ReadScope(m.GetProperty("scope"));
    }
    private static (byte[] Bytes, NavalReadScopeOptions Scope, string Hash) ReadPresentation(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "naval-publication.json", "presentation.json" })) throw Incomplete();
        byte[] ReadPublic(string name, int max)
        {
            string path = Path.Combine(directory, name);
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw Incomplete();
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadBounded(f, max);
        }
        using var d = JsonDocument.Parse(ReadPublic("presentation.json", 65536)); var m = d.RootElement;
        NavalAccessPolicy.Fields(m, new[] { "schema", "publicationSha256", "inputSha256", "coreSha256", "backtesterSha256", "scope" });
        if (m.GetProperty("schema").GetString() != "apex-presentation-v1") throw Incomplete();
        foreach (string name in new[] { "publicationSha256", "inputSha256", "coreSha256", "backtesterSha256" }) if (!LowerHash(m.GetProperty(name).GetString())) throw Incomplete();
        var scope = ReadScope(m.GetProperty("scope")); byte[] bytes = ReadPublic("naval-publication.json", NavalPublicationReader.MaximumBytes);
        string hash = Digest(bytes); if (hash != m.GetProperty("publicationSha256").GetString()) throw Incomplete();
        var envelope = NavalPublicationReader.Parse(bytes, ToScope(scope));
        if (envelope.Provenance.InputSha256 != m.GetProperty("inputSha256").GetString() || envelope.Provenance.CoreSha256 != m.GetProperty("coreSha256").GetString() || envelope.Provenance.BacktesterSha256 != m.GetProperty("backtesterSha256").GetString()) throw Incomplete();
        ValidateSynthetic(envelope, scope);
        return (bytes, scope, hash);
    }
    private static void ValidateSynthetic(NavalPublicationEnvelope envelope, NavalReadScopeOptions scope)
    {
        if (scope.Accounts!.Any(a => a.Venue != "SYNTHETIC" || a.Environment.ToString() != "Offline")) throw Incomplete();
        var c = envelope.Publication.Journal.Checkpoint;
        if (c.BrokerState != "NOT_CONFIGURED" || c.BrokerAccountId != null || c.BrokerEquity != null || c.RealizedPnl != null || c.UnrealizedPnl != null) throw Incomplete();
    }
    private static NavalReadScopeOptions ReadScope(JsonElement s)
    {
        NavalAccessPolicy.Fields(s, new[] { "Id", "PublicationScopeId", "PortfolioId", "ConfigDigest", "Accounts" });
        NavalAccessPolicy.Fields(s.GetProperty("PortfolioId"), new[] { "PortfolioId", "ReportingCurrency" });
        foreach (var a in s.GetProperty("Accounts").EnumerateArray()) NavalAccessPolicy.Fields(a, new[] { "Venue", "Environment", "AccountId", "QuoteCurrency" });
        var scope = JsonSerializer.Deserialize<NavalReadScopeOptions>(s.GetRawText(), Strict) ?? throw Incomplete();
        if (scope.Id != "presentation" || !NavalAccessPolicy.Hex(scope.PublicationScopeId) || !NavalAccessPolicy.Hex(scope.ConfigDigest) || scope.PortfolioId == null || scope.Accounts is not { Length: > 0 }) throw Incomplete();
        return scope;
    }
    private static NavalReadScope ToScope(NavalReadScopeOptions s) => new(s.Id!, s.PublicationScopeId!, s.PortfolioId!, s.ConfigDigest!, s.Accounts!);
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool LowerHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static byte[] ReadPrivate(string path, int maximum)
    {
        NavalPrivatePath.Check(path, true, true);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        WindowsPathSecurity.Validate(new FileInfo(path), true);
        return ReadBounded(f, maximum);
    }
    private static byte[] ReadBounded(FileStream f, int maximum)
    {
        if (f.Length <= 0 || f.Length > maximum) throw Incomplete();
        byte[] bytes = new byte[(int)f.Length]; f.ReadExactly(bytes);
        if (f.ReadByte() != -1) throw Incomplete();
        return bytes;
    }
}
