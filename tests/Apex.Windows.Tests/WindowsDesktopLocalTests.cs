using System.Net;
using System.Text;
using Apex.Desktop;
using Xunit;

namespace Apex.Windows.Tests;

public sealed partial class WindowsLocalSetupTests
{
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
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Program.Run(new[] { "--data", f.Root, "--port", port.ToString() }, false,
                (_, _) => { prompts++; return false; }, request =>
                {
                    launches++;
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
                    stop.Cancel();
                }, stop.Token, null).WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(0, prompts); // old production returns 2 after invoking this forbidden prompt
            Assert.Equal(0, result); Assert.Equal(1, launches);
            Assert.False(await SetupListening(port));
            if (privateBefore != null)
                foreach (var p in privateBefore) Assert.Equal(p.Value, f.Snapshot()[p.Key]);
            privateBefore ??= f.Snapshot();
        }
    }
}
