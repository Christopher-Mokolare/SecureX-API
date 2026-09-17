using System.Diagnostics;
using System.Security.Claims;
using SecureX.Api.Data;
using SecureX.Api.Services;

namespace SecureX.Api.Middleware;

/// <summary>
/// Logs API/webhook requests and persists server-side failures for the admin incident view.
/// Request bodies are never persisted to avoid leaking PII or financial data.
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
        var correlationId = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        ctx.Response.Headers["X-Correlation-ID"] = correlationId;

        var sw = Stopwatch.StartNew();
        try
        {
            await next(ctx);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var provider = DetectProvider(path);
            var txId = TryGetTransactionId(path);

            logger.LogError(ex,
                "[HTTP] {Method} {Path} -> 500 in {Ms}ms | correlation={CorrelationId} ip={Ip} provider={Provider}",
                method, path, sw.ElapsedMilliseconds, correlationId, ip, provider ?? "none");

            await PersistFailureAsync(
                ctx, method, path, 500, ex.Message, ex.GetType().Name, ex.ToString(),
                ex.StackTrace, correlationId, userId, provider, txId);
            throw;
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
                "[HTTP] {Method} {Path} -> {Status} in {Ms}ms | jwt={HasJwt} dealToken={HasDealToken} ip={Ip} ua={UserAgent} correlation={CorrelationId}",
                method, path, status, sw.ElapsedMilliseconds,
                hasJwt, hasDealToken, ip, uaShort, correlationId);

            if (status >= 500)
            {
                var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var provider = DetectProvider(path);
                var txId = TryGetTransactionId(path);
                await PersistFailureAsync(
                    ctx, method, path, status, $"HTTP {status} returned by API", null, null,
                    null, correlationId, userId, provider, txId);
            }
        }
    }

    private static async Task PersistFailureAsync(
        HttpContext ctx,
        string method,
        string path,
        int statusCode,
        string message,
        string? errorType,
        string? exception,
        string? stackTrace,
        string? correlationId,
        string? userId,
        string? provider,
        Guid? transactionId)
    {
        try
        {
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            var service = new SystemFailureLogService(db);
            var severity = statusCode >= 500 ? "Critical" : "Error";
            var category = statusCode >= 500 ? "System" : "HTTP";
            await service.RecordAsync(
                severity,
                category,
                "SecureX API",
                ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName,
                method,
                path,
                statusCode,
                message,
                errorType,
                exception,
                stackTrace,
                correlationId,
                userId,
                provider,
                transactionId,
                ctx.RequestAborted);
        }
        catch
        {
            // Never mask the original API failure with a logging failure.
        }
    }

    private static string? DetectProvider(string path)
    {
        if (path.Contains("ozow", StringComparison.OrdinalIgnoreCase)) return "Ozow";
        if (path.Contains("smile", StringComparison.OrdinalIgnoreCase) || path.Contains("kyc", StringComparison.OrdinalIgnoreCase)) return "SmileID";
        if (path.Contains("thisisme", StringComparison.OrdinalIgnoreCase) || path.Contains("bank", StringComparison.OrdinalIgnoreCase)) return "ThisIsMe";
        if (path.Contains("ses", StringComparison.OrdinalIgnoreCase) || path.Contains("email", StringComparison.OrdinalIgnoreCase)) return "AWS SES";
        return null;
    }

    private static Guid? TryGetTransactionId(string path)
    {
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(segment, out var id)) return id;
        return null;
    }
}
