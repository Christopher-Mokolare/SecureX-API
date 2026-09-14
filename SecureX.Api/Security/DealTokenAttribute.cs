using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SecureX.Api.Services;

namespace SecureX.Api.Security;

/// <summary>
/// Authorizes a request based on a signed deal token presented in the
/// X-Deal-Token header. Usage:
///
///   [DealToken("buyer")]   — caller must be the buyer on the deal
///   [DealToken("seller")]  — caller must be the seller on the deal
///
/// On success, the validated claims are placed in
/// HttpContext.Items["DealClaims"] as a DealTokenService.DealClaims.
///
/// On failure returns 401 (missing/invalid token) or 403 (wrong party).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DealTokenAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string ItemKey = "DealClaims";
    public const string HeaderName = "X-Deal-Token";

    private readonly string _expectedParty;

    public DealTokenAttribute(string expectedParty)
    {
        if (expectedParty is not ("buyer" or "seller"))
            throw new ArgumentException(
                $"expectedParty must be 'buyer' or 'seller' (got '{expectedParty}')",
                nameof(expectedParty));
        _expectedParty = expectedParty;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        // Admin bypass — admins can act on any deal via their JWT
        if (http.User?.IsInRole("Admin") == true)
            return Task.CompletedTask;

        var token = http.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            Reject(context, StatusCodes.Status401Unauthorized, "Missing X-Deal-Token header");
            return Task.CompletedTask;
        }

        var svc = http.RequestServices.GetRequiredService<DealTokenService>();
        var claims = svc.Validate(token);
        if (claims is null)
        {
            Reject(context, StatusCodes.Status401Unauthorized, "Invalid or expired deal token");
            return Task.CompletedTask;
        }

        if (!string.Equals(claims.Party, _expectedParty, StringComparison.OrdinalIgnoreCase))
        {
            Reject(context, StatusCodes.Status403Forbidden,
                $"Deal token is for '{claims.Party}', endpoint requires '{_expectedParty}'");
            return Task.CompletedTask;
        }

        http.Items[ItemKey] = claims;
        return Task.CompletedTask;
    }

    private static void Reject(AuthorizationFilterContext ctx, int status, string message)
    {
        ctx.Result = new ObjectResult(new { error = message }) { StatusCode = status };
    }

    // ── Helpers for controllers ─────────────────────────────────────────────
    public static DealTokenService.DealClaims? GetClaims(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var v) && v is DealTokenService.DealClaims c ? c : null;
}
