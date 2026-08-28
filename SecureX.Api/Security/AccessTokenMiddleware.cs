using Microsoft.Extensions.Logging;

namespace SecureX.Api.Security;

public class AccessTokenMiddleware(RequestDelegate next, IConfiguration config, ILogger<AccessTokenMiddleware> logger)
{
    private readonly string _expected = (config["Ozow:AccessToken"] ?? "").Trim();

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Guard payout webhooks — payment-notification uses hash verification instead
        if (ctx.Request.Path.StartsWithSegments("/securex") &&
            !ctx.Request.Path.StartsWithSegments("/securex/payment-notification") &&
            !ctx.Request.Path.StartsWithSegments("/securex/payout-verify") &&
            !ctx.Request.Path.StartsWithSegments("/securex/payout-notification"))
        {
            var received = ctx.Request.Headers["AccessToken"].FirstOrDefault()
                        ?? ctx.Request.Headers["Authorization"].FirstOrDefault();

            var token = received?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? received["Bearer ".Length..].Trim()
                : received?.Trim();

            if (string.IsNullOrEmpty(_expected) || token != _expected)
            {
                logger.LogWarning("AccessToken rejected on {Path}", ctx.Request.Path);
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }

        await next(ctx);
    }
}
