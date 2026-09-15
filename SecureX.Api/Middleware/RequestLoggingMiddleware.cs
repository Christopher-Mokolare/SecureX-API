using System.Diagnostics;

namespace SecureX.Api.Middleware;

/// <summary>
/// Logs every API and webhook request with its outcome, auth-header presence,
/// and timing. Fills the gap where attribute-level authorization (e.g.
/// [Authorize], [DealToken]) rejects a request before the controller body
/// runs, leaving no log trace of why.
///
/// One log line per request. No request body content is logged (PII risk).
/// </summary>
public class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/securex/", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        var method = ctx.Request.Method;
        var hasJwt = ctx.Request.Headers.ContainsKey("Authorization");
        var hasDealToken = ctx.Request.Headers.ContainsKey("X-Deal-Token");
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var uaShort = ua.Length > 100 ? ua[..100] : ua;

        var sw = Stopwatch.StartNew();
        try
        {
            await next(ctx);
        }
        finally
        {
            sw.Stop();
            var status = ctx.Response.StatusCode;
            var level = status >= 500 ? LogLevel.Error
                      : status >= 400 ? LogLevel.Warning
                      : LogLevel.Information;

            logger.Log(
                level,
                "[HTTP] {Method} {Path} -> {Status} in {Ms}ms | jwt={HasJwt} dealToken={HasDealToken} ip={Ip} ua={UserAgent}",
                method, path, status, sw.ElapsedMilliseconds,
                hasJwt, hasDealToken, ip, uaShort);
        }
    }
}
