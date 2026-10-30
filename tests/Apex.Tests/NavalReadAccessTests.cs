using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Apex.Tests;

public sealed class NavalReadAccessTests
{
    [Fact]
    public void NavalReadAccess_DefaultConfigCanReloadWithoutNavalProvisioning()
    {
        string dir = Path.Combine(Path.GetTempPath(), "apex-naval-task6a-host", "config-" + Guid.NewGuid().ToString("N")); NavalProductionHost.PrivateDirectory(dir);
        try { var first = new Apex.Desktop.Services.ConfigStore(dir); var second = new Apex.Desktop.Services.ConfigStore(dir); Assert.Null(second.Config.NavalRead); } finally { Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("nullScope")]
    [InlineData("invalidPathType")]
    [InlineData("missingEnabled")]
    [InlineData("duplicateConfig")]
    public async Task NavalReadAccess_InvalidRawOptionsFailWithSafeCode(string fault)
    {
        await using var h = new NavalProductionHost();
        if (fault == "nullScope") h.Configuration["NavalRead"]!["Scope"] = null;
        if (fault == "invalidPathType") h.Configuration["NavalRead"]!["CredentialFilePath"] = 123;
        if (fault == "missingEnabled") h.Configuration["NavalRead"]!.AsObject().Remove("Enabled");
        if (fault == "duplicateConfig") h.Configuration["navalread"] = h.Configuration["NavalRead"]!.DeepClone();
        await h.Start(expectExit: true); Assert.Equal(2, h.Process!.ExitCode); Assert.True(await h.SafeOutput());
    }
    [Fact]
    public async Task NavalReadAccess_MalformedAmbiguousAndForgedIdentityCannotUpgrade()
    {
        await using var h = new NavalProductionHost(); await h.Start();
        foreach (var header in new[] { "Bearer x", "Basic !!!", "Bearer " + h.ReadSecret + ", Bearer " + h.ReadSecret, "Bearer " + h.ReadSecret + "=", "Bearer " + new string('a', 4096) })
        { using var r = h.Request("/api/naval/snapshot"); r.Headers.TryAddWithoutValidation("Authorization", header); Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(r)).StatusCode); }
        using (var r = h.Request("/api/naval/snapshot", "read")) { r.Headers.Add("X-Apex-Token", h.LegacyToken); Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(r)).StatusCode); }
        using (var r = h.Request("/api/status", "read")) { r.Headers.Add("X-Role", "Operator"); r.Headers.Add("X-Forwarded-User", "operator"); r.Headers.Add("X-Forwarded-Host", "localhost:" + h.Port); Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.SendAsync(r)).StatusCode); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("/?token=" + "not-a-credential")).StatusCode);
    }
    [Theory]
    [InlineData("emptyPrincipals")]
    [InlineData("noOperator")]
    [InlineData("noReader")]
    [InlineData("sameDigest")]
    [InlineData("sameId")]
    [InlineData("foreignGrant")]
    [InlineData("emptyGrant")]
    [InlineData("unknownField")]
    [InlineData("missingField")]
    [InlineData("nullDigest")]
    [InlineData("uppercaseDigest")]
    [InlineData("tooLarge")]
    [InlineData("symlink")]
    public async Task NavalReadAccess_PrivateDigestDocumentIsStrict(string fault)
    {
        await using var h = new NavalProductionHost(); var n = JsonNode.Parse(File.ReadAllText(h.CredentialPath))!; var ps = n["principals"]!.AsArray();
        switch (fault)
        {
            case "emptyPrincipals": ps.Clear(); break;
            case "noOperator": ps[0]!["role"] = "ReadOnly"; break;
            case "noReader": ps[1]!["role"] = "Operator"; break;
            case "sameDigest": ps[1]!["secretSha256"] = ps[0]!["secretSha256"]!.DeepClone(); break;
            case "sameId": ps[1]!["id"] = ps[0]!["id"]!.DeepClone(); break;
            case "foreignGrant": ps[1]!["allowedScopeIds"]![0] = "foreign"; break;
            case "emptyGrant": ps[1]!["allowedScopeIds"]!.AsArray().Clear(); break;
            case "unknownField": ps[1]!["secret"] = "not-a-secret"; break;
            case "missingField": ps[1]!.AsObject().Remove("role"); break;
            case "nullDigest": ps[1]!["secretSha256"] = null; break;
            case "uppercaseDigest": ps[1]!["secretSha256"] = ps[1]!["secretSha256"]!.GetValue<string>().ToUpperInvariant(); break;
        }
        File.WriteAllText(h.CredentialPath, n.ToJsonString());
        if (fault == "tooLarge") File.AppendAllText(h.CredentialPath, new string(' ', 65536));
        if (fault == "symlink") { string target = h.CredentialPath + ".target"; File.Move(h.CredentialPath, target); File.CreateSymbolicLink(h.CredentialPath, target); }
        await h.Start(expectExit: true); Assert.Equal(2, h.Process!.ExitCode);
    }
    [Fact]
    public async Task NavalReadAccess_DisabledOptionsDoNotOpenFilesAndCredentialsAreFrozen()
    {
        await using (var disabled = new NavalProductionHost())
        { disabled.Configuration["NavalRead"]!["Enabled"] = false; File.Delete(disabled.CredentialPath); await disabled.Start(); Assert.Equal(HttpStatusCode.OK, (await disabled.Send("/")).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await disabled.Send("/api/naval/snapshot", "legacy")).StatusCode); }
        await using var h = new NavalProductionHost(); await h.Start(); File.WriteAllText(h.CredentialPath, "{"); Assert.Equal(HttpStatusCode.OK, (await h.Send("/api/naval/snapshot", "read")).StatusCode); await h.Stop(); await h.Start(expectExit: true); Assert.Equal(2, h.Process!.ExitCode);
    }
    [Fact]
    public async Task NavalReadAccess_DefaultModePreservesOldPanel()
    {
        await using var h = new NavalProductionHost(false); await h.Start();
        var root = await h.Send("/"); Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.True((await root.Content.ReadAsStringAsync()).IndexOf(h.LegacyToken, StringComparison.Ordinal) >= 0);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Send("/api/naval/snapshot", "legacy")).StatusCode);
        using (var status=JsonDocument.Parse(await (await h.Send("/api/status", "legacy")).Content.ReadAsStringAsync())) { Assert.Empty(status.RootElement.GetProperty("data").GetProperty("accounts").EnumerateArray()); Assert.False(status.RootElement.GetProperty("host").GetProperty("liveFeature").GetBoolean()); }
        foreach(string asset in new[]{"app.js","decision-center.js","app.css"}) Assert.Equal(File.ReadAllBytes(Path.Combine(NavalProductionHost.DesktopDirectory,"wwwroot",asset)),await (await h.Send("/"+asset)).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await h.Send("/api/accounts/missing/command", "legacy", "POST")).StatusCode);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("permissions")]
    [InlineData("weak")]
    [InlineData("options")]
    public async Task NavalReadAccess_EnabledWithoutCredentialsNeverDowngrades(string fault)
    {
        await using var h = new NavalProductionHost();
        if (fault == "missing") File.Delete(h.CredentialPath);
        if (fault == "corrupt") File.WriteAllText(h.CredentialPath, "{");
        if (fault == "unknown") { var n = JsonNode.Parse(File.ReadAllText(h.CredentialPath))!; n["unknown"] = true; File.WriteAllText(h.CredentialPath, n.ToJsonString()); }
        if (fault == "duplicate") File.WriteAllText(h.CredentialPath, File.ReadAllText(h.CredentialPath).Replace("\"schema\":", "\"schema\":\"naval-local-access-v1\",\"schema\":"));
        if (fault == "permissions") { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(h.CredentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); else { var info=new FileInfo(h.CredentialPath);var acl=info.GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.Read,AccessControlType.Allow));info.SetAccessControl(acl); } }
        if (fault == "weak") File.WriteAllText(h.CredentialPath, File.ReadAllText(h.CredentialPath).Replace("ReadOnly", "Admin"));
        if (fault == "options") h.Configuration["NavalRead"]!["unexpected"] = true;
        await h.Start(expectExit: true); Assert.Equal(2, h.Process!.ExitCode);
        Assert.False(await h.Listening());
    }
    [Fact]
    public async Task NavalReadAccess_ProductionCredentialsAuthenticateAndScope()
    {
        await using var h = new NavalProductionHost(); await h.Start();
        foreach (var auth in new[] { "", "invalid", "legacy", "readBasic" }) Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("/api/naval/snapshot", auth)).StatusCode);
        foreach (var auth in new[] { "read", "operator" })
        {
            Assert.Equal(HttpStatusCode.OK, (await h.Send("/api/naval/snapshot?scope=offline", auth)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Send("/api/naval/snapshot?scope=foreign", auth)).StatusCode);
        }
    }
    [Fact]
    public async Task NavalReadAccess_BootstrapAndLegacyCannotEscalate()
    {
        await using var h = new NavalProductionHost(); await h.Start();
        foreach (var path in new[] { "/", "/index.html", "/app.js", "/api/status", "/api/accounts/missing/decisions", "/api/accounts/missing/journal.csv", "/unknown", "/bridge/other" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Send(path, "read")).StatusCode);
        }
        foreach (var path in new[] { "/", "/index.html" })
        {
            var r = await h.Send(path, "operator"); Assert.Equal(HttpStatusCode.OK, r.StatusCode); var html = await r.Content.ReadAsStringAsync();
            Assert.True(html.IndexOf("name=\"apex-token\" content=\"\"", StringComparison.Ordinal) >= 0);
            Assert.True(!html.Contains(h.LegacyToken) && !html.Contains(h.ReadSecret) && !html.Contains(h.OperatorSecret) && !html.Contains("__APEX_TOKEN__"));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Send("/bridge", "read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Send("/bridge", "operator")).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavalReadAccess_NoNavalWriterAndOperatorEmergencySurvives(bool enabled)
    {
        await using var h = new NavalProductionHost(enabled);
        if (enabled) { await NavalHostPublication.Cli(h); File.WriteAllText(NavalHostPublication.PathFor(h), "{"); }
        await h.Start(); string auth = enabled ? "operator" : "legacy";
        if (enabled)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode);
            foreach (string path in new[] { "/api/naval/command", "/api/naval/start", "/api/naval/execution-test", "/api/naval/receipt" })
            { using var req = h.Request(path, auth, "POST"); req.Content = new StringContent("{}", Encoding.UTF8, "application/json"); Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(req)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await h.Send(path, "read", "POST")).StatusCode); }
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await h.Send("/api/naval/snapshot", auth, "POST")).StatusCode);
        }
        using var ws = new System.Net.WebSockets.ClientWebSocket(); using var ct = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{h.Port}/bridge"), ct.Token);
        async Task Send<T>(T msg) => await ws.SendAsync(Encoding.UTF8.GetBytes(Apex.Core.Bridge.BridgeProtocol.Serialize(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, ct.Token);
        var account = new Apex.Core.Bridge.AccountDto { Number = 930001, Broker = "Pepperstone", Currency = "USD", Balance = 10000, Equity = 10000, FreeMargin = 10000 };
        await Send(new Apex.Core.Bridge.HelloMsg { Key = "TEST-PAIRING", Platform = "cTrader", Account = account });
        var buffer = new byte[65536]; var welcome = await ws.ReceiveAsync(buffer, ct.Token); using (var d = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, welcome.Count))) Assert.True(d.RootElement.GetProperty("accepted").GetBoolean());
        await Send(new Apex.Core.Bridge.SymbolsMsg()); await Send(new Apex.Core.Bridge.AccountMsg { Account = account });
        await Send(new Apex.Core.Bridge.PositionsMsg { Items = new() { new Apex.Core.Bridge.PositionDto { Id = 77, Symbol = "US500", Side = "buy", Label = "APEX", Entry = 5000, Price = 5000, Sl = 4900, Tp = 5300, Units = .1 }, new Apex.Core.Bridge.PositionDto { Id = 78, Symbol = "US500", Side = "buy", Label = "MANUAL", Entry = 5000, Price = 5000, Sl = 4900, Tp = 5300, Units = .1 } } });
        JsonElement found = default;
        for (int i = 0; i < 100; i++)
        { using var response = await h.Send("/api/status", auth); using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var accounts = status.RootElement.GetProperty("data").GetProperty("accounts"); if (accounts.GetArrayLength() > 0 && accounts[0].GetProperty("engineReady").GetBoolean()) { found = accounts[0].Clone(); break; } await Task.Delay(50); }
        Assert.Equal(JsonValueKind.Object, found.ValueKind); string key = Uri.EscapeDataString(found.GetProperty("key").GetString()!);
        using var command = h.Request("/api/accounts/" + key + "/command", auth, "POST"); command.Content = new StringContent("{\"action\":\"emergency\"}", Encoding.UTF8, "application/json");
        var commandTask = h.Client.SendAsync(command);
        bool ownClosed = false;
        while (!ownClosed)
        {
            var frame = await ws.ReceiveAsync(buffer, ct.Token); using var d = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, frame.Count));
            if (d.RootElement.GetProperty("type").GetString() == "close")
            {
                Assert.Equal(77, d.RootElement.GetProperty("positionId").GetInt64()); Assert.True(!d.RootElement.TryGetProperty("units", out var units) || units.ValueKind == JsonValueKind.Null);
                await Send(new Apex.Core.Bridge.ResultMsg { Id = d.RootElement.GetProperty("id").GetString()!, Status = "filled", PositionId = 77, Price = 5000 }); ownClosed = true;
            }
        }
        using (var result = await commandTask) Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        bool emergencyObserved = false;
        for (int i = 0; i < 100; i++)
        {
            using var response = await h.Send("/api/status", auth); using var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (d.RootElement.GetProperty("data").GetProperty("accounts")[0].GetProperty("engine").GetProperty("mode").GetString() == "EmergencyStopped") { emergencyObserved = true; break; }
            await Task.Delay(50);
        }
        Assert.True(emergencyObserved, "Emergency status did not publish");
        string control = Directory.GetFiles(Path.Combine(h.DirectoryPath, "konta"), "desktop_control.json", SearchOption.AllDirectories).Single();
        Assert.True(JsonDocument.Parse(File.ReadAllText(control)).RootElement.GetProperty("EmergencyStopped").GetBoolean());
        ws.Abort();
    }
    [Fact]
    public async Task NavalReadAccess_OnlyExactGetContractsAndUniformErrorHeaders()
    {
        await using var h = new NavalProductionHost(); await h.Start();
        foreach (var method in new[] { "POST", "PUT", "DELETE", "HEAD", "OPTIONS" })
        {
            var r = await h.Send("/api/naval/snapshot", "operator", method); Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode); Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType); Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        }
        foreach (var path in new[] { "/api/naval/SNAPSHOT", "/api/naval/snapshot/" }) Assert.Equal(HttpStatusCode.NotFound, (await h.Send(path, "operator")).StatusCode);
        using var redirect = await h.Send("/naval", "operator");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/naval/", redirect.Headers.Location!.OriginalString);
        Assert.Equal("no-store", redirect.Headers.CacheControl!.ToString());
        Assert.Equal("DENY", redirect.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", redirect.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", redirect.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'", redirect.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(redirect.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Send("/naval", "read")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("/naval")).StatusCode);
    }
    [Fact]
    public async Task NavalReadAccess_ExactAuthenticatedRootNavalRedirect()
    {
        await using var h = new NavalProductionHost(); await h.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("/?view=naval")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Send("/?view=naval", "read")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send("/?view=naval", "invalid")).StatusCode);
        using var redirect = await h.Send("/?view=naval", "operator");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/naval/", redirect.Headers.Location!.OriginalString);
        Assert.True(redirect.Headers.CacheControl!.NoStore);
        foreach (string query in new[] { "", "?view=Naval", "?view=naval&next=https://evil.test", "?view=naval&view=naval", "?view=%6eaval", "?view=//evil.test", "?VIEW=naval", "?view=naval/", "?next=naval" })
        {
            using var response = await h.Send("/" + query, "operator");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(response.Headers.Location);
            string html = await response.Content.ReadAsStringAsync();
            Assert.Contains("name=\"apex-token\" content=\"\"", html);
            Assert.True(!html.Contains(h.ReadSecret) && !html.Contains(h.OperatorSecret) && !html.Contains(h.LegacyToken));
        }
        using var alias = await h.Send("/index.html?view=naval", "operator"); Assert.Equal(HttpStatusCode.OK, alias.StatusCode); Assert.Null(alias.Headers.Location);
        await using var old = new NavalProductionHost(false); await old.Start();
        Assert.Equal(HttpStatusCode.OK, (await old.Send("/?view=naval")).StatusCode);
    }
    [Fact]
    public async Task NavalReadAccess_HostHeadersAndCrossSite()
    {
        await using var h = new NavalProductionHost(); await h.Start();
        foreach (var auth in new[] { "", "read", "operator" })
        {
            var r = await h.Send("/api/status", auth); Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
            Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single()); Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
        }
        using var badHost = h.Request("/", "operator"); badHost.Headers.Host = "evil.test:" + h.Port; Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.SendAsync(badHost)).StatusCode);
        foreach (var origin in new[] { "null", "https://evil.test", "http://127.0.0.1:1" })
        {
            using var req = h.Request("/api/accounts/missing/command", "operator", "POST"); req.Content = new StringContent("{}", Encoding.UTF8, "application/json"); req.Headers.Add("Origin", origin);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.SendAsync(req)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await h.Send("/api/accounts/missing/command", "operator", "POST")).StatusCode);
        using var same = h.Request("/api/accounts/missing/command", "operator", "POST"); same.Headers.Add("Origin", h.Client.BaseAddress!.GetLeftPart(UriPartial.Authority)); same.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.SendAsync(same)).StatusCode);
    }
}

internal sealed class NavalProductionHost : IAsyncDisposable
{
    internal static string Repo { get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !File.Exists(Path.Combine(d.FullName, "APEX.Trading.Demo.sln"))) d = d.Parent; return d!.FullName; } }
    internal static string DesktopDirectory
    {
        get
        {
            string? package=Environment.GetEnvironmentVariable("APEX_PACKAGE_ROOT");
            if (package==null) return Path.Combine(Repo,"src","Apex.Desktop","bin","Release","net8.0");
            if (string.IsNullOrWhiteSpace(package) || !Path.IsPathFullyQualified(package)) throw new InvalidOperationException("Invalid explicit package root");
            string root=Path.GetFullPath(package), desktop=Path.Combine(root,"APEX-Desktop");
            var meta=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"apex-build.json")))!;
            string expected=OperatingSystem.IsWindows()?"win-x64":"linux-x64";
            if (meta["schema"]?.GetValue<string>()!="apex-build-v2" || meta["target"]?.GetValue<string>()!=expected || (File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0 || (File.GetAttributes(desktop)&FileAttributes.ReparsePoint)!=0 || !File.Exists(Path.Combine(desktop,OperatingSystem.IsWindows()?"ApexDesktop.exe":"ApexDesktop.dll"))) throw new InvalidOperationException("Package target/path refused; no repository fallback");
            return desktop;
        }
    }
    internal string DirectoryPath { get; }
    internal string CredentialPath { get; }
    internal string ReadSecret { get; } = Secret(); internal string OperatorSecret { get; } = Secret();
    internal string LegacyToken { get; } = Secret();
    internal JsonObject Configuration { get; }
    internal int Port { get; } = FreePort();
    internal HttpClient Client { get; }
    internal Process? Process { get; private set; }
    private Task<string>? output, error;
    internal NavalProductionHost(bool enabled = true)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "apex-naval-task6a-host", "process-" + Guid.NewGuid().ToString("N")); PrivateDirectory(DirectoryPath);
        var secrets = Path.Combine(DirectoryPath, "private"); PrivateDirectory(secrets); CredentialPath = Path.Combine(secrets, "access.json");
        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(new { schema = "naval-local-access-v1", principals = new[] { new { id = "operator", role = "Operator", secretSha256 = Hash(OperatorSecret), allowedScopeIds = new[] { "offline" } }, new { id = "reader", role = "ReadOnly", secretSha256 = Hash(ReadSecret), allowedScopeIds = new[] { "offline" } } } })); PrivateFile(CredentialPath);
        Configuration = JsonSerializer.SerializeToNode(new { HttpPort = Port, BridgeTcpPort = FreePort(), ApiToken = LegacyToken, PairingKey = "TEST-PAIRING", OpenBrowserOnStart = false, News = new { Enabled = false } })!.AsObject();
        if (enabled) Configuration["NavalRead"] = JsonSerializer.SerializeToNode(new { Enabled = true, PublicationPath = (string?)null, CredentialFilePath = CredentialPath, Scope = new { Id = "offline", PublicationScopeId = new string('a', 64), PortfolioId = new { PortfolioId = "test", ReportingCurrency = "USD" }, ConfigDigest = new string('b', 64), Accounts = new[] { new { Venue = "Offline", Environment = "Offline", AccountId = "test", QuoteCurrency = "USD" } } } });
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(2) };
    }
    internal async Task Start(bool expectExit = false)
    {
        File.WriteAllText(Path.Combine(DirectoryPath, "apex.config.json"), Configuration.ToJsonString());
        string desktop=DesktopDirectory;
        bool native=Environment.GetEnvironmentVariable("APEX_PACKAGE_ROOT")!=null && OperatingSystem.IsWindows();
        string executable=native?Path.Combine(desktop,"ApexDesktop.exe"):(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet");
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory=desktop };
        if (!native) psi.ArgumentList.Add(Path.Combine(desktop,"ApexDesktop.dll"));
        psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(DirectoryPath); psi.ArgumentList.Add("--no-browser");
        Process = System.Diagnostics.Process.Start(psi)!; output = Process.StandardOutput.ReadToEndAsync(); error = Process.StandardError.ReadToEndAsync();
        Evidence("process-"+Guid.NewGuid().ToString("N"),new {pid=Process.Id,executable,desktop,packaged=Environment.GetEnvironmentVariable("APEX_PACKAGE_ROOT")!=null,payloadSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(desktop,native?"ApexDesktop.exe":"ApexDesktop.dll")))).ToLowerInvariant(),ownedPrivateData=true,noBrowser=true});
        for (int i = 0; i < 200; i++) { if (Process.HasExited) { if (expectExit) return; throw new InvalidOperationException("Production host exited before readiness (output withheld)"); } if (await Listening()) { if (expectExit) Assert.Fail("Enabled invalid config bound HTTP instead of exiting closed"); return; } await Task.Delay(50); }
        throw new TimeoutException("Production host readiness deadline");
    }
    internal async Task<bool> Listening() { try { using var r = await Client.GetAsync("/"); return true; } catch (HttpRequestException) { return false; } catch (TaskCanceledException) { return false; } }
    internal HttpRequestMessage Request(string path, string auth = "", string method = "GET")
    {
        var r = new HttpRequestMessage(new HttpMethod(method), path);
        if (auth == "read") r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadSecret);
        if (auth == "operator" || auth == "readBasic") r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(auth == "operator" ? "operator:" + OperatorSecret : "reader:" + ReadSecret)));
        if (auth == "legacy") r.Headers.Add("X-Apex-Token", LegacyToken);
        if (auth == "invalid") r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('x', 43)); return r;
    }
    internal async Task<HttpResponseMessage> Send(string path, string auth = "", string method = "GET")
    {
        using var r = Request(path, auth, method); var response = await Client.SendAsync(r);
        Evidence("http-" + Guid.NewGuid().ToString("N"), new { method, path, role = auth, status = (int)response.StatusCode, noStore = response.Headers.CacheControl?.NoStore ?? false, contentType = response.Content.Headers.ContentType?.MediaType });
        if (path.StartsWith("/api/naval/", StringComparison.Ordinal) && response.Content.Headers.ContentType?.MediaType == "application/json")
        { string body = await response.Content.ReadAsStringAsync(); Assert.True(!body.Contains(ReadSecret) && !body.Contains(OperatorSecret) && !body.Contains(LegacyToken)); if (!string.IsNullOrEmpty(body)) Evidence("naval-" + Guid.NewGuid().ToString("N"), JsonNode.Parse(body)); }
        return response;
    }
    internal static void Evidence(string name, object? value)
    {
        string? directory = Environment.GetEnvironmentVariable("NAVAL_HOST_EVIDENCE_DIR"); if (directory == null) return;
        PrivateDirectory(directory); File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(value));
    }
    internal async Task<bool> SafeOutput()
    {
        if (Process == null || !Process.HasExited) return false; string text = await output! + await error!;
        return !text.Contains(ReadSecret) && !text.Contains(OperatorSecret) && !text.Contains(LegacyToken) && !text.Contains(Hash(ReadSecret)) && !text.Contains(Hash(OperatorSecret)) && !text.Contains("Authorization:");
    }
    internal async Task Stop() { if (Process != null) { if (!Process.HasExited) { Process.Kill(true); await Process.WaitForExitAsync(); } await Task.WhenAll(output!, error!); Assert.True(await SafeOutput(), "Production output contains secret material"); Process.Dispose(); Process = null; } }
    public async ValueTask DisposeAsync() { await Stop(); Client.Dispose(); if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, true); }
    internal static void PrivateDirectory(string p) { System.IO.Directory.CreateDirectory(p); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); else { var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false); var user=WindowsIdentity.GetCurrent().User!;acl.SetOwner(user);foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(p).SetAccessControl(acl); } }
    internal static void PrivateFile(string p) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite); else { var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);var user=WindowsIdentity.GetCurrent().User!;acl.SetOwner(user);foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));new FileInfo(p).SetAccessControl(acl); } }
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(s))).ToLowerInvariant();
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
}
