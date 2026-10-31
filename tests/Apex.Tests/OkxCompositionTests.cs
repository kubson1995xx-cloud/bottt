using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
[Collection("OKX composition")]
public sealed class OkxCompositionTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OkxComposition_DefaultAndConfiguredBootStayStopped(bool configured)
    {
        await using var h = new OkxCompositionFixture();
        string raw = h.Raw(configured ? OkxCompositionFixture.ValidConfig(NavalEnvironment.OkxDemo) : null);
        Assert.Equal(0, await h.StartHostAsync(raw)); Assert.True(h.ReachedHttp);
        Assert.True(h.Resources.ContainsKey("okx"), "Actual production host must own the stopped OKX runtime");
        var runtime = Assert.IsType<OkxNavalRuntime>(h.Resources["okx"]);
        Assert.Equal("STOP", runtime.Mode); Assert.Equal(configured ? "ACTIVATION_REQUIRED" : "NOT_CONFIGURED", runtime.Reason);
        Assert.False(((Lazy<IOkxExecutionSecretProvider>)typeof(OkxNavalRuntime).GetField("secrets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!).IsValueCreated);
        Assert.False(Directory.Exists(Path.Combine(h.Root, "okx")));
        var before = h.Inventory();
        Assert.Equal(0, await h.RestartHostAsync()); Assert.True(h.ReachedHttp);
        var restarted = Assert.IsType<OkxNavalRuntime>(h.Resources["okx"]);
        Assert.NotSame(runtime, restarted); Assert.Equal("STOP", restarted.Mode); Assert.Equal(before, h.Inventory());
        Assert.False(Directory.Exists(Path.Combine(h.Root, "okx")));
    }
    [Fact]
    public async Task FrozenCompositionNeverOpensSecretsAndDisposalRevokes()
    {
        await using var h = new OkxCompositionFixture();
        var binding = CredentialFixture.Binding(); var bindings = new[] { binding }; var paths = OkxConfiguration.Paths(h.Root, binding);
        var secrets = new SecretSpy(); var protectedProvider = new ActivationSpy();
        var runtime = OkxNavalRuntime.Compose(h.Root, bindings, secrets, protectedProvider);
        bindings[0] = CredentialFixture.Binding(NavalEnvironment.Live);
        await runtime.StartAsync(CancellationToken.None);
        Assert.Equal(binding, Assert.Single(runtime.Bindings)); Assert.Equal("STOP", runtime.Mode); Assert.Equal("ACTIVATION_REQUIRED", runtime.Reason);
        Assert.Equal(0, secrets.OpenCount); Assert.Equal(0, protectedProvider.Calls); Assert.False(Directory.Exists(paths.Authority));
        var now = DateTimeOffset.UtcNow; var request = new OkxActivationRequest(binding, new string('a', 64), Guid.NewGuid(), 1, OkxActivationPurpose.Execute, now.AddSeconds(60));
        var lease = await runtime.Activation.RequestAsync(request, CancellationToken.None); Assert.NotNull(lease);
        await runtime.DisposeAsync(); await runtime.DisposeAsync();
        Assert.False(runtime.Activation.TryConsume(lease!, request, DateTimeOffset.UtcNow));
        Assert.Null(await runtime.Activation.RequestAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runtime.StartAsync(CancellationToken.None));
        Assert.Equal(0, secrets.OpenCount); Assert.False(Directory.Exists(paths.Root));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OkxComposition_ProductionIgnoresSyntheticUnlocks(bool trustedSynthetic)
    {
        await using var h = new OkxCompositionFixture();
        string raw = h.Raw(OkxCompositionFixture.ValidConfig(NavalEnvironment.Live));
        raw = raw[..^1] + ",\"TrustedSyntheticComposition\":true,\"AllowLive\":true}";
        raw = raw.Replace("\"LiveTradingFeature\": false", "\"LiveTradingFeature\": true");
        int port = JsonDocument.Parse(raw).RootElement.GetProperty("HttpPort").GetInt32();
        HttpStatusCode? status = null;
        bool activationDeniedWhileRunning = false;
        h.HttpAction = () => {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Add("X-Apex-Token", new string('a', 64));
            using var response = client.PostAsync($"http://127.0.0.1:{port}/api/naval/okx/synthetic-live/activate", new StringContent("{\"confirmed\":true,\"unlockLive\":true}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            status = response.StatusCode;
            var active = (OkxNavalRuntime)h.Resources["okx"]!; var during = DateTimeOffset.UtcNow;
            activationDeniedWhileRunning = active.Activation.RequestAsync(new(CredentialFixture.Binding(NavalEnvironment.Live), new string('a', 64), Guid.NewGuid(), 1, OkxActivationPurpose.Execute, during.AddSeconds(60)), CancellationToken.None).GetAwaiter().GetResult() == null;
        };
        Assert.Equal(0, await h.StartHostAsync(raw, trustedSynthetic)); Assert.True(h.ReachedHttp);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.True(activationDeniedWhileRunning, "Actual running production composition must deny even a well-formed direct activation request");
        Assert.True(h.Resources.ContainsKey("okx")); var runtime = Assert.IsType<OkxNavalRuntime>(h.Resources["okx"]);
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await runtime.Activation.RequestAsync(new(CredentialFixture.Binding(NavalEnvironment.Live), new string('a', 64), Guid.NewGuid(), 1, OkxActivationPurpose.Execute, now.AddSeconds(60)), CancellationToken.None));
        Assert.Equal("STOP", runtime.Mode); Assert.False(Directory.Exists(Path.Combine(h.Root, "okx")));
    }
    [Fact]
    public async Task OkxComposition_MalformedConfigPreservesExistingBytes()
    {
        await using var h = new OkxCompositionFixture(); var store = new ConfigStore(h.Root);
        byte[] before = File.ReadAllBytes(store.Path_); var old = store.Config;
        Assert.ThrowsAny<Exception>(() => store.Update(c => c.OkxNaval = new OkxNavalOptions { Schema = "bad", Scopes = Array.Empty<OkxScopeOptions>() }));
        Assert.Same(old, store.Config); Assert.Equal(before, File.ReadAllBytes(store.Path_));
        string raw = h.Raw("null"); File.WriteAllText(h.ConfigPath, raw); var inventory = h.Inventory();
        Assert.Equal(2, await h.StartHostAsync(raw)); Assert.Equal(inventory, h.Inventory()); Assert.Empty(h.Resources);
    }
    [Fact]
    public async Task OkxComposition_ProtectedOperatorCannotActivate()
    {
        await using var h = new NavalProductionHost();
        h.Configuration["OkxNaval"] = System.Text.Json.Nodes.JsonNode.Parse(OkxCompositionFixture.ValidConfig(NavalEnvironment.Live));
        await h.Start();
        using (var ready = await h.Send("/api/status", "operator")) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        foreach (string role in new[] { "operator", "read" })
        {
            using var request = h.Request("/api/naval/okx/synthetic-live/activate", role, "POST");
            request.Content = new StringContent("{\"confirmed\":true,\"purpose\":\"execute\",\"unlockLive\":true}", System.Text.Encoding.UTF8, "application/json");
            using var response = await h.Client.SendAsync(request);
            Assert.Equal(role == "operator" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.False(Directory.Exists(Path.Combine(h.DirectoryPath, "okx")));
    }
    [Fact]
    public async Task CancellationDuringActualHostCompositionDisposesWithoutFinancialState()
    {
        await using var h = new OkxCompositionFixture { CancelAtOkx = true };
        Assert.Equal(0, await h.StartHostAsync(h.Raw(OkxCompositionFixture.ValidConfig(NavalEnvironment.OkxDemo))));
        var runtime = Assert.IsType<OkxNavalRuntime>(h.Resources["okx"]);
        Assert.False(h.ReachedHttp); Assert.Single(h.Resources); Assert.False(Directory.Exists(Path.Combine(h.Root, "okx")));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runtime.StartAsync(CancellationToken.None));
    }
}
