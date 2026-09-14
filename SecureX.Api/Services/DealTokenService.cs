using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureX.Api.Services;

/// <summary>
/// Issues and validates per-transaction "deal tokens" for buyers and sellers.
///
/// Buyers and sellers do NOT have accounts. They interact with the platform
/// via emailed links that carry a token binding them to a specific deal.
/// This service is the sole authority on those tokens.
///
/// Token format:  base64url(payload).base64url(hmac-sha256(payload))
/// Payload JSON:  { "dealRef", "party", "txId", "iat", "exp" }
///
/// Deliberately NOT a JWT: no header, no alg field, no claim namespace.
/// Nobody should mistake these tokens for session credentials.
/// </summary>
public class DealTokenService(IConfiguration config, ILogger<DealTokenService> logger)
{
    private const string PartyBuyer = "buyer";
    private const string PartySeller = "seller";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private byte[] SecretBytes
    {
        get
        {
            var secret = config["DealToken:Secret"];
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException(
                    "DealToken:Secret is not configured. Set DEAL_TOKEN_SECRET env var or DealToken:Secret in config.");
            return Encoding.UTF8.GetBytes(secret);
        }
    }

    private int ExpiryDays =>
        int.TryParse(config["DealToken:ExpiryDays"], out var d) && d > 0 ? d : 7;

    // ── Public API ──────────────────────────────────────────────────────────

    public string GenerateBuyerToken(string dealReference, Guid transactionId)
        => Generate(dealReference, PartyBuyer, transactionId);

    public string GenerateSellerToken(string dealReference, Guid transactionId)
        => Generate(dealReference, PartySeller, transactionId);

    /// <summary>
    /// Verifies signature and expiry. Returns claims on success, null on any
    /// failure (bad format, bad signature, expired). Never throws on bad input.
    /// </summary>
    public DealClaims? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            logger.LogWarning("DealToken: malformed (wrong segment count)");
            return null;
        }

        byte[] payloadBytes;
        byte[] providedSig;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            providedSig  = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            logger.LogWarning("DealToken: malformed (base64 decode failed)");
            return null;
        }

        // Constant-time signature comparison
        byte[] expectedSig;
        try
        {
            using var hmac = new HMACSHA256(SecretBytes);
            expectedSig = hmac.ComputeHash(payloadBytes);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "DealToken: cannot compute signature (secret missing)");
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
        {
            logger.LogWarning("DealToken: signature mismatch");
            return null;
        }

        DealClaims? claims;
        try
        {
            claims = JsonSerializer.Deserialize<DealClaims>(payloadBytes, JsonOpts);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "DealToken: payload not valid JSON");
            return null;
        }

        if (claims is null)
        {
            logger.LogWarning("DealToken: payload deserialized to null");
            return null;
        }

        if (string.IsNullOrWhiteSpace(claims.DealRef) ||
            string.IsNullOrWhiteSpace(claims.Party) ||
            claims.TxId == Guid.Empty)
        {
            logger.LogWarning("DealToken: payload missing required fields");
            return null;
        }

        if (claims.Party is not (PartyBuyer or PartySeller))
        {
            logger.LogWarning("DealToken: unknown party '{Party}'", claims.Party);
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (claims.Exp <= now)
        {
            logger.LogWarning("DealToken: expired (exp={Exp}, now={Now})", claims.Exp, now);
            return null;
        }

        return claims;
    }

    // ── Internal ────────────────────────────────────────────────────────────

    private string Generate(string dealReference, string party, Guid transactionId)
    {
        if (string.IsNullOrWhiteSpace(dealReference))
            throw new ArgumentException("dealReference required", nameof(dealReference));
        if (party is not (PartyBuyer or PartySeller))
            throw new ArgumentException($"unknown party '{party}'", nameof(party));
        if (transactionId == Guid.Empty)
            throw new ArgumentException("transactionId required", nameof(transactionId));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new DealClaims
        {
            DealRef = dealReference,
            Party   = party,
            TxId    = transactionId,
            Iat     = now,
            Exp     = now + (ExpiryDays * 86400L),
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(claims, JsonOpts);

        byte[] sig;
        using (var hmac = new HMACSHA256(SecretBytes))
            sig = hmac.ComputeHash(payloadBytes);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(sig)}";
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Claims carried by a validated deal token. A buyer or seller proves
    /// possession of this by presenting the token; the platform binds them
    /// to a specific deal via DealRef.
    /// </summary>
    public sealed class DealClaims
    {
        public string DealRef { get; set; } = "";
        public string Party   { get; set; } = "";
        public Guid   TxId    { get; set; }
        public long   Iat     { get; set; }
        public long   Exp     { get; set; }
    }
}
