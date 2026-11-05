using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Desktop.Naval;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace Apex.Desktop.Okx;
internal static class OkxRemoteControllerEndpoints
{
    internal static readonly string[] Actions={"connect","disconnect","connect-account","prepare","arm","start","stop","emergency","refresh"};
    internal static bool IsStatus(string? path)=>path=="/api/naval/controller/status";
    internal static bool IsPath(string? path)=>IsStatus(path)||Actions.Any(a=>path=="/api/naval/controller/"+a);
    internal static void Map(WebApplication app,OkxRemoteControllerControl control,INavalAccessPolicy access,NavalReadScope scope)
    {
        foreach(string asset in new[]{"remote-controller.js","remote-controller.css"})
        {
            string name=asset;
            app.MapGet("/"+name,(HttpContext ctx)=>ctx.Request.Path.Value!="/"+name?Results.NotFound():Results.Stream(typeof(OkxRemoteControllerEndpoints).Assembly.GetManifestResourceStream("Apex.Desktop.Okx."+name)!,name.EndsWith(".js",StringComparison.Ordinal)?"text/javascript; charset=utf-8":"text/css; charset=utf-8"));
        }
        app.MapGet("/api/naval/controller/status",(HttpContext ctx)=>
        {
            if(!IsStatus(ctx.Request.Path.Value))return Results.NotFound();
            if(access.Authorize(ctx,scope)==NavalReadAccess.Denied)return Results.StatusCode(403);
            if(ctx.Request.QueryString.HasValue)return Results.BadRequest();
            ctx.Response.Headers["Cache-Control"]="no-store";return Results.Json(control.ReadStatus());
        });
        foreach(string value in Actions)
        {
            string action=value;
            app.MapPost("/api/naval/controller/"+action,async(HttpContext ctx)=>
            {
                if(ctx.Request.Path.Value!="/api/naval/controller/"+action)return Results.NotFound();
                if(access.Authorize(ctx,scope)!=NavalReadAccess.Operator)return Results.StatusCode(403);
                ctx.Response.Headers["Cache-Control"]="no-store";
                if(ctx.Request.Headers["X-Apex-Local"].Count!=1||ctx.Request.Headers["X-Apex-Local"]!="1")return Results.StatusCode(403);
                if(ctx.Request.QueryString.HasValue)return Results.BadRequest(new{code="CLOSED_COMMAND_REQUIRED"});
                if(ctx.Request.ContentType is not ("application/json" or "application/json; charset=utf-8"))return Results.StatusCode(415);
                string id;
                try
                {
                    using var body=new MemoryStream();byte[] buffer=new byte[512];int n;
                    while((n=await ctx.Request.Body.ReadAsync(buffer,ctx.RequestAborted))>0){if(body.Length+n>512)return Results.BadRequest(new{code="COMMAND_LIMIT"});body.Write(buffer,0,n);}
                    using var d=JsonDocument.Parse(body.ToArray(),new JsonDocumentOptions{MaxDepth=2});var root=d.RootElement;
                    if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=1||!root.TryGetProperty("commandId",out var property)||property.ValueKind!=JsonValueKind.String)return Results.BadRequest(new{code="CLOSED_COMMAND_REQUIRED"});
                    id=property.GetString()!;if(!OkxConfiguration.Identifier(id))return Results.BadRequest(new{code="COMMAND_ID_INVALID"});
                }
                catch(JsonException){return Results.BadRequest(new{code="CLOSED_COMMAND_REQUIRED"});}
                // HTTP/request cancellation cannot automatically retry or recast an uncertain mutation as success.
                return Results.Json(await control.ExecuteAsync(action,id));
            });
        }
    }
}
