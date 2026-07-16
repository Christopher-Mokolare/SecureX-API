namespace SecureX.Api.Security;

public class AccessTokenMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly string _expected = (config["Ozow:AccessToken"] ?? "").Trim();

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Only guard the Ozow webhook paths
        if (ctx.Request.Path.StartsWithSegments("/securex"))
        {
            var received = ctx.Request.Headers["AccessToken"].FirstOrDefault()
                        ?? ctx.Request.Headers["Authorization"].FirstOrDefault();

            var token = received?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? received["Bearer ".Length..].Trim()
                : received?.Trim();

            if (string.IsNullOrEmpty(_expected) || token != _expected)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }

        await next(ctx);
    }
}
