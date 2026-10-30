using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
namespace Apex.Tests;
[CollectionDefinition("NavalFrontendHostSerial", DisableParallelization = true)]
public sealed class NavalFrontendHostSerialCollection { }

// Missing-index coverage temporarily mutates the shared copied build tree, never run
// it concurrently with production-host consumers from any other test collection.
[Collection("NavalFrontendHostSerial")]
public sealed class NavalFrontendHostTests
{
    [Fact] public void Frontend_ExplicitPackageRootCannotFallBackToRepo()
    {
        string? package=Environment.GetEnvironmentVariable("APEX_PACKAGE_ROOT");
        if (package!=null) Assert.Equal(Path.GetFullPath(Path.Combine(package,"APEX-Desktop","wwwroot")),Webroot);
    }
    private static string Webroot => Path.Combine(NavalProductionHost.DesktopDirectory,"wwwroot");
    private static JsonNode Manifest => JsonNode.Parse(File.ReadAllText(Path.Combine(Webroot,"naval","build-manifest.json")))!;
    [Fact] public async Task Frontend_EnabledOperatorGetsBuildAndHashAssets()
    {
        await using var h=new NavalProductionHost(); await h.Start();
        var redirect=await h.Send("/naval","operator"); Assert.Equal(HttpStatusCode.Redirect,redirect.StatusCode);Assert.Equal("/naval/",redirect.Headers.Location!.OriginalString);
        foreach(var doc in new[]{"/naval/","/naval/index.html"}) {var r=await h.Send(doc,"operator");Assert.Equal(HttpStatusCode.OK,r.StatusCode);Assert.Equal("text/html",r.Content.Headers.ContentType!.MediaType);Assert.True(r.Headers.CacheControl!.NoStore);Assert.Contains("AWARYJNY STOP",await r.Content.ReadAsStringAsync());}
        foreach(var entry in Manifest["artifacts"]!.AsArray()){string p=entry!["path"]!.GetValue<string>();if(!p.StartsWith("assets/",StringComparison.Ordinal))continue;var r=await h.Send("/naval/"+p,"operator");Assert.Equal(HttpStatusCode.OK,r.StatusCode);byte[] bytes=await r.Content.ReadAsByteArrayAsync();Assert.Equal(entry["bytes"]!.GetValue<int>(),bytes.Length);Assert.Equal(entry["sha256"]!.GetValue<string>(),Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());}
    }
    [Fact] public async Task Frontend_ReadOnlyStillOnlyTwoGets()
    {
        await using var h=new NavalProductionHost();await h.Start();
        foreach(var path in new[]{"/naval","/naval/","/naval/index.html"}.Concat(Manifest["artifacts"]!.AsArray().Where(x=>x!["path"]!.GetValue<string>().StartsWith("assets/",StringComparison.Ordinal)).Select(x=>"/naval/"+x!["path"]!.GetValue<string>()))) {Assert.Equal(HttpStatusCode.Forbidden,(await h.Send(path,"read")).StatusCode);Assert.Equal(HttpStatusCode.Unauthorized,(await h.Send(path)).StatusCode);}
        Assert.Equal(HttpStatusCode.OK,(await h.Send("/api/naval/snapshot","read")).StatusCode);
    }
    [Fact] public async Task Frontend_MissingBuildDoesNotBreakOldPanel()
    {
        // Isolate missing-index mutation from other frontend cases; no legacy/static source changes.
        string p=Path.Combine(Webroot,"naval","index.html"),backup=p+".test-backup";byte[] original=File.ReadAllBytes(p),inventory=File.ReadAllBytes(Path.Combine(Webroot,"naval","build-manifest.json"));File.Move(p,backup);
        try{await using var h=new NavalProductionHost();await h.Start();var r=await h.Send("/naval/","operator");Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);string body=await r.Content.ReadAsStringAsync();Assert.Contains("NAVAL_UI_NOT_BUILT",body);Assert.Contains("href=\"/\"",body);Assert.DoesNotContain(Webroot,body);Assert.Equal(HttpStatusCode.OK,(await h.Send("/","operator")).StatusCode);}finally{File.Move(backup,p);Assert.Equal(original,File.ReadAllBytes(p));Assert.Equal(inventory,File.ReadAllBytes(Path.Combine(Webroot,"naval","build-manifest.json")));}
    }
    [Fact] public async Task Frontend_NoCatchAllOrSecretBootstrap()
    {
        await using var h=new NavalProductionHost();await h.Start();foreach(var path in new[]{"/naval/unknown","/naval/assets/missing.js","/naval/assets/missing.css","/naval/INDEX.html","/NAVAL/","/naval//","/other-route"})Assert.Equal(HttpStatusCode.NotFound,(await h.Send(path,"operator")).StatusCode);
        var r=await h.Send("/naval/","operator");string body=await r.Content.ReadAsStringAsync();Assert.DoesNotContain(h.OperatorSecret,body);Assert.DoesNotContain(h.ReadSecret,body);Assert.DoesNotContain(h.LegacyToken,body);Assert.DoesNotContain("apex-token",body);Assert.Contains("/naval/assets/",body);Assert.Equal(HttpStatusCode.MethodNotAllowed,(await h.Send("/naval/","operator","POST")).StatusCode);
    }
    [Fact] public async Task Frontend_DisabledModePreservesOriginalPanel()
    {
        await using var h=new NavalProductionHost(false);await h.Start();Assert.Equal(HttpStatusCode.NotFound,(await h.Send("/naval")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await h.Send("/naval/")).StatusCode);var root=await h.Send("/");Assert.Equal(HttpStatusCode.OK,root.StatusCode);Assert.Contains("APEX Trading AI",await root.Content.ReadAsStringAsync());Assert.Equal(HttpStatusCode.NotFound,(await h.Send("/api/naval/snapshot","legacy")).StatusCode);
    }
}
