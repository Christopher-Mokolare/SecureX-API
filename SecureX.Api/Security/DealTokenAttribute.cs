using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SecureX.Api.Services;
using System.Security.Claims;

namespace SecureX.Api.Security;

/// <summary>
/// Authorizes a request based on a signed deal token (X-Deal-Token header)
/// and/or a legacy JWT (Authorization: Bearer), for endpoints that serve
/// buyers and sellers of a specific transaction.
///
/// Usage:
///   [DealToken("buyer")]               — buyer only
///   [DealToken("seller")]              — seller only
///   [DealToken("buyer", "seller")]     — either party
///
/// Behaviour:
///   1. If X-Deal-Token header is present: it MUST be valid. On failure → 401.
///      On success, claims are placed in HttpContext.Items["DealClaims"].
///   2. If header is absent, fall back to a legacy JWT in Authorization
///      (validate by presence of a NameIdentifier claim). This lets old
///      clients keep working until they're migrated. Sets
///      HttpContext.Items["LegacyJwt"] = true.
///   3. Neither present → 401.
///
/// A present-but-invalid deal token NEVER falls back to JWT — this
/// prevents a tampered token from being silently downgraded.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DealTokenAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string ItemKey         = "DealClaims";
    public const string LegacyJwtKey    = "LegacyJwt";
    public const string HeaderName      = "X-Deal-Token";

    private static readonly string[] AllowedParties = { "buyer", "seller" };

    private readonly string[] _expectedParties;

    public DealTokenAttribute(params string[] expectedParties)
    {
        if (expectedParties is null || expectedParties.Length == 0)
            throw new ArgumentException("At least one expected party is required", nameof(expectedParties));

        foreach (var p in expectedParties)
        {
            if (!AllowedParties.Contains(p, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Expected party must be 'buyer' or 'seller' (got '{p}')",
                    nameof(expectedParties));
        }
        _expectedParties = expectedParties.Select(p => p.ToLowerInvariant()).ToArray();
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        // ── Admin bypass — admins can act on any deal via their JWT ────────
        if (http.User?.IsInRole("Admin") == true)
            return Task.CompletedTask;

        var dealToken = http.Request.Headers[HeaderName].FirstOrDefault();

        // ── Path 1: deal token present — must be valid ─────────────────────
        if (!string.IsNullOrWhiteSpace(dealToken))
        {
            var svc = http.RequestServices.GetRequiredService<DealTokenService>();
            var claims = svc.Validate(dealToken);
            if (claims is null)
            {
                Reject(context, StatusCodes.Status401Unauthorized, "Invalid or expired deal token");
                return Task.CompletedTask;
            }

            if (!_expectedParties.Contains(claims.Party.ToLowerInvariant()))
            {
                Reject(context, StatusCodes.Status403Forbidden,
                    $"Deal token is for '{claims.Party}', endpoint requires one of: {string.Join(", ", _expectedParties)}");
                return Task.CompletedTask;
            }

            http.Items[ItemKey] = claims;
            return Task.CompletedTask;
        }

        // ── Path 2: no deal token — legacy JWT fallback ────────────────────
        // Only require that a JWT authenticated the caller. Ownership checks
        // against the transaction happen in the controller (it has tx context).
        var userIdClaim = http.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out _))
        {
            http.Items[LegacyJwtKey] = true;
            return Task.CompletedTask;
        }

        // ── Path 3: neither ────────────────────────────────────────────────
        Reject(context, StatusCodes.Status401Unauthorized, "Missing X-Deal-Token header and no valid JWT");
        return Task.CompletedTask;
    }

    private static void Reject(AuthorizationFilterContext ctx, int status, string message)
    {
        ctx.Result = new ObjectResult(new { error = message }) { StatusCode = status };
    }

    // ── Helpers for controllers ─────────────────────────────────────────────

    /// <summary>
    /// Returns validated deal claims if the request used X-Deal-Token.
    /// Returns null if the request used the legacy JWT fallback path.
    /// </summary>
    public static DealTokenService.DealClaims? GetClaims(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var v) && v is DealTokenService.DealClaims c ? c : null;

    /// <summary>
    /// True if the request was authorized by the legacy JWT path (not a deal token).
    /// </summary>
    public static bool UsedLegacyJwt(HttpContext http) =>
        http.Items.TryGetValue(LegacyJwtKey, out var v) && v is true;

    /// <summary>
    /// Resolves the effective caller identity for a transaction, given the
    /// deal-token and legacy-jwt paths. Returns null if neither path
    /// identifies a party on this transaction.
    /// </summary>
    public static Guid? ResolveCallerUserId(
        HttpContext http,
        Guid buyerId,
        Guid sellerId)
    {
        var claims = GetClaims(http);
        if (claims is not null)
        {
            return claims.Party.ToLowerInvariant() switch
            {
                "buyer"  => buyerId,
                "seller" => sellerId,
                _        => null,
            };
        }

        if (UsedLegacyJwt(http))
        {
            var uid = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(uid, out var userId))
            {
                if (userId == buyerId || userId == sellerId) return userId;
                // Admin via JWT fallback: allowed only if they hold Admin role —
                // but by this point OnAuthorizationAsync has already short-circuited
                // for admins, so this branch means JWT isn't a party on the tx.
                if (http.User.IsInRole("Admin")) return userId;
            }
        }

        return null;
    }
}
