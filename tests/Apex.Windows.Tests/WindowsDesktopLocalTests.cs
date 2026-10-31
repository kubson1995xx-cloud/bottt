using System.Net;
using System.Text;
using Apex.Desktop;
using Xunit;

namespace Apex.Windows.Tests;

public sealed partial class WindowsLocalSetupTests
{
    [Fact, Trait("Gate", "Source")]
    public async Task DesktopLocalBrowserBoundaryAndExplicitProtectedOptOut()
    {
        using var f = new SetupFixture(); Ensure(f.Root);
        int port = FreeSetupPort();
        var secrets = Secrets(f.Root);
        string cfgPath = Path.Combine(f.Root, "apex.config.json");
        Exception? browserFailure = null;
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            int exit = await Program.Run(new[] { "--data", f.Root, "--port", port.ToString() }, false,
                (_, _) => throw new Exception("No startup prompt allowed"), _ =>
                {
                    try
                    {
                    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                    void Check(string path, string method, int expected, string? header = null, string? value = null, bool csrf = false, string media = "application/json")
                    {
                        using var request = new HttpRequestMessage(new HttpMethod(method), path);
                        if (header != null) request.Headers.TryAddWithoutValidation(header, value);
                        if (csrf) request.Headers.Add("X-Apex-Local", "1");
                        if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, media);
                        using var response = http.SendAsync(request).GetAwaiter().GetResult();
                        Assert.Equal(expected, (int)response.StatusCode);
                        Assert.Empty(response.Headers.WwwAuthenticate);
                        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
                    }
                    Check("/", "GET", 200);
                    Check("/api/status", "GET", 403, "Host", "evil.test:" + port);
                    foreach (string origin in new[] { "https://evil.test", "null", $"http://localhost:{port}", $"http://127.0.0.1:{port + 1}" })
                    {
                        Check("/", "GET", 403, "Origin", origin);
                        Check("/api/accounts/missing/command", "POST", 403, "Origin", origin, true);
                    }
                    foreach (string site in new[] { "cross-site", "same-site", "invalid", "same-origin, none" })
                    {
                        Check("/api/naval/snapshot", "GET", 403, "Sec-Fetch-Site", site);
                        Check("/api/accounts/missing/command", "POST", 403, "Sec-Fetch-Site", site, true);
                    }
                    Check("/api/accounts/missing/command", "POST", 403); // no custom-header CSRF proof
                    Check("/api/accounts/missing/command", "POST", 415, csrf: true, media: "text/plain");
                    Check("/api/accounts/missing/command", "POST", 415, csrf: true, media: "application/x-www-form-urlencoded");
                    Check("/api/accounts/missing/command", "POST", 404, "Origin", $"http://127.0.0.1:{port}", true);
                    Check("/api/accounts/missing/command", "OPTIONS", 403, "Origin", "https://evil.test");
                    Check("/api/naval/snapshot", "GET", 200, "Authorization", "Bearer " + secrets.Reader);
                    Check("/", "GET", 403, "Authorization", "Bearer " + secrets.Reader);
                    Check("/api/accounts/missing/command", "POST", 403, "Authorization", "Bearer " + secrets.Reader, true);
                    Check("/", "GET", 401, "Authorization", "Basic invalid");
                    Check("/bridge", "GET", 403, "Origin", $"http://127.0.0.1:{port}");
                    }
                    catch (Exception e) { browserFailure = e; }
                    finally { stop.Cancel(); }
                }, stop.Token, null);
            Assert.Null(browserFailure);
            Assert.Equal(0, exit);
        }
        // Explicit opt-out on even a genuine Ready profile is not downgraded.
        var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(cfgPath))!;
        cfg["NavalRead"]!["RequireAuthentication"] = true;
        File.WriteAllText(cfgPath, cfg.ToJsonString()); var before = f.Snapshot();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            int exit = await Program.Run(new[] { "--data", f.Root, "--port", port.ToString() }, false, null, _ =>
            {
                try
                {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                using var anonymous = http.GetAsync("/").GetAwaiter().GetResult();
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.Equal("Basic", anonymous.Headers.WwwAuthenticate.Single().Scheme);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("operator:" + secrets.Operator)));
                using var authenticated = http.GetAsync("/").GetAwaiter().GetResult(); Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
                }
                catch (Exception e) { browserFailure = e; }
                finally { stop.Cancel(); }
            }, stop.Token, null);
            Assert.Null(browserFailure);
            Assert.Equal(0, exit);
        }
        foreach (var pair in before.Where(p => !p.Key.StartsWith("logi" + Path.DirectorySeparatorChar))) Assert.Equal(pair.Value, f.Snapshot()[pair.Key]);
    }

    // Uses real Program admission/lock/setup/HTTP. Only the shell is observed;
    // actual no-interception browser proof belongs to the extracted EXE gate.
    [Theory, Trait("Gate", "Source")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopLocalFirstAndLegacyReadyRestartNeverNeedManualLogin(bool legacyReady)
    {
        using var f = new SetupFixture();
        if (legacyReady) Ensure(f.Root); // unchanged v1 producer contract; no config edits/reset
        var privateBefore = legacyReady ? f.Snapshot() : null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int port = FreeSetupPort(), prompts = 0, launches = 0;
            Exception? browserFailure = null;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Program.Run(new[] { "--data", f.Root, "--port", port.ToString() }, false,
                (_, _) => { prompts++; return false; }, request =>
                {
                    launches++;
                    try
                    {
                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(3) };
                    using var redirect = client.GetAsync("/?view=naval").GetAwaiter().GetResult();
                    Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
                    Assert.Empty(redirect.Headers.WwwAuthenticate);
                    Assert.Equal("/naval/", redirect.Headers.Location!.OriginalString);
                    foreach (string path in new[] { "/", "/naval/", "/app.js", "/api/status", "/api/naval/snapshot" })
                    {
                        using var response = client.GetAsync(path).GetAwaiter().GetResult();
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Empty(response.Headers.WwwAuthenticate);
                    }
                    }
                    catch (Exception e) { browserFailure = e; }
                    finally { stop.Cancel(); }
                }, stop.Token, null).WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Null(browserFailure);
            Assert.Equal(0, prompts); // old production returns 2 after invoking this forbidden prompt
            Assert.Equal(0, result); Assert.Equal(1, launches);
            Assert.False(await SetupListening(port));
            if (privateBefore != null)
                foreach (var p in privateBefore) Assert.Equal(p.Value, f.Snapshot()[p.Key]);
            privateBefore ??= f.Snapshot().Where(p => !p.Key.StartsWith("logi" + Path.DirectorySeparatorChar)).ToDictionary(p => p.Key, p => p.Value);
        }
    }
}
