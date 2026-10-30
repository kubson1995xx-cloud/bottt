using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Apex.Desktop.Naval;
internal static class NavalReadEndpoints
{
    internal static void MapNavalReadEndpoints(WebApplication app, NavalReadService service, INavalAccessPolicy access)
    {
        app.MapGet("/api/naval/snapshot", async (HttpContext ctx) =>
        {
            if (!await Validate(ctx, service, access, false)) return;
            string? generation = ctx.Request.Query["requestGeneration"].FirstOrDefault();
            var result = await service.SnapshotAsync(service.Scope, DateTimeOffset.UtcNow, generation, ctx.RequestAborted);
            await Write(ctx, result.StatusCode, result.Body);
        });
        app.MapGet("/api/naval/events", async (HttpContext ctx) =>
        {
            if (!await Validate(ctx, service, access, true)) return;
            NavalCursor cursor;
            try { cursor = NavalCursor.Decode(ctx.Request.Query["cursor"].ToString()); } catch (ArgumentException) { await Error(ctx, service, 400, "INVALID_QUERY"); return; }
            // A cursor is never authority, even if the pure Core helper would return a gap.
            if (cursor.ScopeId != service.Scope.PublicationScopeId) { await Error(ctx, service, 403, "ACCESS_DENIED"); return; }
            int limit = ctx.Request.Query.ContainsKey("limit") ? int.Parse(ctx.Request.Query["limit"].ToString(), CultureInfo.InvariantCulture) : 100;
            var result = await service.EventsAsync(service.Scope, ctx.Request.Query["hostEpoch"].ToString(), cursor, limit, DateTimeOffset.UtcNow, ctx.Request.Query["requestGeneration"].FirstOrDefault(), ctx.RequestAborted);
            await Write(ctx, result.StatusCode, result.Body);
        });
    }
    private static async Task<bool> Validate(HttpContext ctx, NavalReadService service, INavalAccessPolicy access, bool events)
    {
        if (access.Authorize(ctx, service.Scope) == NavalReadAccess.Denied) { await Error(ctx, service, 403, "ACCESS_DENIED"); return false; }
        var allowed = events ? new[] { "scope", "requestGeneration", "hostEpoch", "cursor", "limit" } : new[] { "scope", "requestGeneration" };
        foreach (var q in ctx.Request.Query)
            if (!allowed.Contains(q.Key, StringComparer.Ordinal) || q.Value.Count != 1) { await Error(ctx, service, 400, "INVALID_QUERY"); return false; }
        if (ctx.Request.Query.TryGetValue("scope", out var scope) && scope.ToString() != service.Scope.Id) { await Error(ctx, service, 403, "ACCESS_DENIED"); return false; }
        if (ctx.Request.Query.TryGetValue("requestGeneration", out var generation) && !NavalAccessPolicy.Identifier(generation.ToString())) { await Error(ctx, service, 400, "INVALID_QUERY"); return false; }
        if (events)
        {
            if (!Guid.TryParseExact(ctx.Request.Query["hostEpoch"].ToString(), "N", out _) || !ctx.Request.Query.ContainsKey("cursor")) { await Error(ctx, service, 400, "INVALID_QUERY"); return false; }
            if (ctx.Request.Query.TryGetValue("limit", out var l) && (!int.TryParse(l.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 100 || n.ToString(CultureInfo.InvariantCulture) != l.ToString())) { await Error(ctx, service, 400, "INVALID_QUERY"); return false; }
        }
        return true;
    }
    private static Task Error(HttpContext ctx, NavalReadService service, int status, string reason) => Write(ctx, status, new NavalEventsDto(new NavalHostEnvelope { HostEpoch = service.HostEpoch, Scope = service.Scope.Id, ServedUtc = DateTimeOffset.UtcNow, ReasonCode = reason }, null, true));
    private static async Task Write<T>(HttpContext ctx, int status, T value)
    {
        ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, NavalPublicationReader.Json), ctx.RequestAborted);
    }
}
