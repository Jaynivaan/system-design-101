using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AgencyHarness.Api;

/// <summary>
/// The "API gateway" concerns from SD101, done in-process as middleware:
/// correlation IDs → authentication → (rate limiting, registered in Program.cs) → routing.
/// In a larger deployment these move to a real gateway (YARP, Envoy, APIM) unchanged in spirit.
/// </summary>
public static class Auth
{
    public const string TenantItem = "tenant";
    public const string ApiKeyHeader = "X-Api-Key";

    public static Tenant Tenant(this HttpContext ctx) =>
        ctx.Items[TenantItem] as Tenant ?? throw new InvalidOperationException("No authenticated tenant.");

    /// <summary>Echo or mint a correlation id and push it into every log line for the request.</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var id = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 and <= 64 } h
            ? h : Guid.NewGuid().ToString("n");
        ctx.TraceIdentifier = id;
        ctx.Response.Headers["X-Correlation-Id"] = id;
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Request");
        using (log.BeginScope("correlation={CorrelationId}", id))
            await next();
    });

    /// <summary>Resolves X-Api-Key to a tenant for /v1 routes. Lookups are cached briefly
    /// so auth doesn't cost a DB round trip on every request.</summary>
    public static IApplicationBuilder UseTenantAuth(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/v1"))
        {
            await next();
            return;
        }

        var key = ctx.Request.Headers[ApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(key))
        {
            await Results.Problem("Missing X-Api-Key header.", statusCode: 401).ExecuteAsync(ctx);
            return;
        }

        var hash = Security.HashApiKey(key);
        var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
        var tenant = await cache.GetOrCreateAsync($"auth:{hash}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var db = ctx.RequestServices.GetRequiredService<HarnessDbContext>();
            return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.ApiKeyHash == hash);
        });

        if (tenant is null)
        {
            await Results.Problem("Invalid API key.", statusCode: 401).ExecuteAsync(ctx);
            return;
        }
        ctx.Items[TenantItem] = tenant;
        await next();
    });
}
