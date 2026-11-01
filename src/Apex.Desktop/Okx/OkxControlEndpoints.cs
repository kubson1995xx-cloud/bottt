using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace Apex.Desktop.Okx;
internal static class OkxControlEndpoints
{
    private static readonly string[] Actions={"connect","disconnect","baseline","arm","start","stop","emergency","recover"};
    internal static bool IsPath(string? path)
    {
        if(path is "/api/naval/okx/status" or "/api/naval/okx/configure")return true;
        var p=path?.Split('/');return p is {Length:6}&&p[1]=="api"&&p[2]=="naval"&&p[3]=="okx"&&OkxConfiguration.Identifier(p[4])&&Actions.Contains(p[5],StringComparer.Ordinal);
    }
    internal static void Map(WebApplication app,OkxOperatorControl control)
    {
        app.MapGet("/okx-controls.js",()=>Results.Stream(typeof(OkxControlEndpoints).Assembly.GetManifestResourceStream("Apex.Desktop.Okx.okx-controls.js")!,"text/javascript; charset=utf-8"));
        app.MapGet("/okx-controls.css",()=>Results.Stream(typeof(OkxControlEndpoints).Assembly.GetManifestResourceStream("Apex.Desktop.Okx.okx-controls.css")!,"text/css; charset=utf-8"));
        app.MapGet("/api/naval/okx/status",()=>Results.Json(control.ReadStatus()));
        app.MapPost("/api/naval/okx/configure",(Delegate)((HttpContext ctx)=>Command(ctx,"", "configure")));
        app.MapPost("/api/naval/okx/{scope}/{action}",(HttpContext ctx,string scope,string action)=>Command(ctx,scope,action));
        async Task<IResult> Command(HttpContext ctx,string scope,string action)
        {
            if(action!="configure"&&(!OkxConfiguration.Identifier(scope)||!Actions.Contains(action,StringComparer.Ordinal)))return Results.NotFound();
            if(ctx.Request.Headers["X-Apex-Local"].Count!=1||ctx.Request.Headers["X-Apex-Local"]!="1")return Results.StatusCode(403);
            if(ctx.Request.QueryString.HasValue)return Results.BadRequest(new{code="CLOSED_COMMAND_REQUIRED"});
            if(!string.Equals(ctx.Request.ContentType,"application/json",StringComparison.OrdinalIgnoreCase)&&!string.Equals(ctx.Request.ContentType,"application/json; charset=utf-8",StringComparison.OrdinalIgnoreCase))return Results.StatusCode(415);
            string id;string? reservation=null;
            try
            {
                using var buffer=new MemoryStream();byte[] bytes=new byte[1024];int n;
                while((n=await ctx.Request.Body.ReadAsync(bytes,ctx.RequestAborted))!=0){if(buffer.Length+n>1024)return Results.BadRequest(new{code="COMMAND_LIMIT"});buffer.Write(bytes,0,n);}
                using var doc=JsonDocument.Parse(buffer.ToArray(),new JsonDocumentOptions{MaxDepth=3});
                OkxConfiguration.Fields(doc.RootElement,action=="recover"?new[]{"commandId","reservationId"}:new[]{"commandId"});
                if(action=="recover")
                {
                    var r=doc.RootElement.GetProperty("reservationId");if(r.ValueKind!=JsonValueKind.String||!OkxConfiguration.Identifier(r.GetString()!))return Results.BadRequest(new{code="EXACT_RESERVATION_REQUIRED"});reservation=r.GetString();
                }
                var value=doc.RootElement.GetProperty("commandId");if(value.ValueKind!=JsonValueKind.String||!OkxConfiguration.Identifier(value.GetString()!))return Results.BadRequest(new{code="COMMAND_ID_INVALID"});id=value.GetString()!;
            }
            catch(Exception e) when(e is JsonException or InvalidDataException){return Results.BadRequest(new{code="CLOSED_COMMAND_REQUIRED"});}
            if(action!="configure"&&!control.Known(scope))return Results.NotFound(new{code="SCOPE_NOT_CONFIGURED"});
            string code=await control.ExecuteAsync(scope,action,id,ctx.RequestAborted,reservation).ConfigureAwait(false);
            return Results.Json(new{code,conditionalPolicyAvailable=true});
        }
    }
}
