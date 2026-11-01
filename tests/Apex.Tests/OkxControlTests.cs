using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Apex.Tests;

public sealed class OkxControlTests
{
    [Fact]
    public async Task Production_status_is_passive_and_reports_missing_protection_without_funded_zeroes()
    {
        await using var h=new NavalProductionHost(); await h.Start();
        using var response=await h.Send("/api/naval/okx/status","operator");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("STOP",doc.RootElement.GetProperty("mode").GetString());
        Assert.False(doc.RootElement.GetProperty("productionProfileApproved").GetBoolean());
        Assert.Equal("PRODUCTION_PROTECTION_PROFILE_UNAPPROVED",doc.RootElement.GetProperty("productionBlocker").GetString());
        Assert.Empty(doc.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.DoesNotContain("fingerprint",doc.RootElement.GetRawText(),StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden,(await h.Send("/api/naval/okx/status","read")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await h.Send("/api/naval/okx/status")).StatusCode);
    }
    [Fact]
    public async Task Configured_scope_without_open_owner_reports_unknown_not_empty_commitments()
    {
        await using var h=new NavalProductionHost();
        h.Configuration["OkxNaval"]=System.Text.Json.Nodes.JsonNode.Parse(OkxCompositionFixture.ValidConfig(Apex.Core.Naval.NavalEnvironment.OkxDemo));
        await h.Start();using var response=await h.Send("/api/naval/okx/status","operator");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var a=d.RootElement.GetProperty("accounts")[0];Assert.Equal(JsonValueKind.Null,a.GetProperty("cash").ValueKind);
        Assert.Equal(JsonValueKind.Null,a.GetProperty("obligations").ValueKind);Assert.False(a.GetProperty("obligationsKnown").GetBoolean());
    }
    [Fact]
    public async Task Existing_panel_loads_controls_and_unknown_scope_commands_never_connect()
    {
        await using var h=new NavalProductionHost(); await h.Start();
        string html=await (await h.Send("/","operator")).Content.ReadAsStringAsync();
        Assert.Contains("/okx-controls.js",html);
        Assert.Equal(HttpStatusCode.OK,(await h.Send("/okx-controls.js","operator")).StatusCode);
        foreach(var action in new[]{"connect","disconnect","baseline","arm","start","stop","emergency"})
        {
            using var req=h.Request("/api/naval/okx/missing/"+action,"operator","POST");
            req.Content=new StringContent("{\"commandId\":\"test-"+action+"\"}",Encoding.UTF8,"application/json");
            req.Headers.Add("X-Apex-Local","1");
            Assert.Equal(HttpStatusCode.NotFound,(await h.Client.SendAsync(req)).StatusCode);
        }
    }
    [Fact]
    public async Task Control_boundary_denies_reader_origin_missing_header_and_secret_fields()
    {
        await using var h=new NavalProductionHost(); await h.Start();
        foreach(var mutation in new[]{"reader","origin","header","secret","duplicate","scalar"})
        {
            using var req=h.Request("/api/naval/okx/missing/start",mutation=="reader"?"read":"operator","POST");
            if(mutation!="header")req.Headers.Add("X-Apex-Local","1");
            if(mutation=="origin")req.Headers.Add("Origin","https://foreign.invalid");
            string body=mutation switch{"secret"=>"{\"commandId\":\"x\",\"secret\":\"must-not-be-accepted\"}","duplicate"=>"{\"commandId\":\"x\",\"commandId\":\"y\"}","scalar"=>"[]",_=>"{\"commandId\":\"x\"}"};
            req.Content=new StringContent(body,Encoding.UTF8,"application/json");
            var status=(await h.Client.SendAsync(req)).StatusCode;
            Assert.Equal(mutation is "reader" or "origin" or "header"?HttpStatusCode.Forbidden:HttpStatusCode.BadRequest,status);
        }
    }
}
