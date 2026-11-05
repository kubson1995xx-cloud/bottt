using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace Apex.Desktop.Naval;
internal static class NavalFrontendEndpoints
{
    internal static void MapNavalFrontendEndpoints(WebApplication app)
    {
        app.MapGet("/naval", async (HttpContext ctx) =>
        {
            // ASP.NET routing ignores the trailing slash; one candidate avoids ambiguity.
            if (ctx.Request.Path.Value == "/naval/") { await Index(ctx); return; }
            if (!string.Equals(ctx.Request.Path.Value, "/naval", StringComparison.Ordinal)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Redirect("/naval/");
        });
        async Task Index(HttpContext ctx)
        {
            string? path = ctx.Request.Path.Value;
            if (path != "/naval/" && path != "/naval/index.html") { ctx.Response.StatusCode = 404; return; }
            string index = Path.Combine(app.Environment.WebRootPath, "naval", "index.html");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (!File.Exists(index))
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("<!doctype html><html lang=\"pl\"><head><meta charset=\"utf-8\"><title>Naval unavailable</title></head><body><h1>NAVAL_UI_NOT_BUILT</h1><a href=\"/\">Otwórz oryginalny panel operatora</a></body></html>"); return;
            }
            await ctx.Response.SendFileAsync(index, ctx.RequestAborted);
        }
        app.MapGet("/naval/index.html", Index);
    }
}
