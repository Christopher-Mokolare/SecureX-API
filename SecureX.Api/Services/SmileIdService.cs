using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Services;

public class SmileIdService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<SmileIdService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private string PartnerId => config["SmileId:PartnerId"]!;
    private string ApiKey    => config["SmileId:ApiKey"]!;
    private string BaseUrl   => config["SmileId:BaseUrl"] ?? "https://testapi.smileidentity.com/v1";
    private string Callback  => config["SmileId:CallbackUrl"]!;

    // ── Generate HMAC-SHA256 signature ───────────────────────────────────────
    // SmileID signature = base64(HMAC-SHA256(timestamp + partner_id + "sid_request", api_key))

    private string BuildSignature(string timestamp)
    {
        var message = $"{timestamp}{PartnerId}sid_request";
        var keyBytes = Encoding.UTF8.GetBytes(ApiKey);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var hash = HMACSHA256.HashData(keyBytes, msgBytes);
        return Convert.ToBase64String(hash);
    }

    // ── Enhanced KYC (ID + liveness) ────────────────────────────────────────
    // Returns a web token the frontend uses to launch the SmileID web integration

    public async Task<string?> GetWebTokenAsync(Guid userId, string callbackUrl)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var body = new
        {
            partner_id    = PartnerId,
            timestamp,
            signature     = BuildSignature(timestamp),
            user_id       = userId.ToString(),
            job_id        = Guid.NewGuid().ToString(),
            product       = "biometric_kyc",
            callback_url  = callbackUrl,
        };

        var client = httpFactory.CreateClient("SmileId");
        var res = await client.PostAsync($"{BaseUrl}/token",
            new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"));

        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("SmileID GetWebToken failed {Status}: {Body}", res.StatusCode, raw);
            return null;
        }

        var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
    }

    // ── AML check ────────────────────────────────────────────────────────────

    public async Task<bool> RunAmlCheckAsync(Guid userId, string fullName, string idNumber)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nameParts = fullName.Trim().Split(' ', 2);
        var body = new
        {
            partner_id   = PartnerId,
            timestamp,
            signature    = BuildSignature(timestamp),
            user_id      = userId.ToString(),
            countries    = new[] { "ZA" },
            full_name    = fullName,
            first_name   = nameParts[0],
            last_name    = nameParts.Length > 1 ? nameParts[1] : "",
            id_number    = idNumber,
            id_type      = "NATIONAL_ID",
        };

        var client = httpFactory.CreateClient("SmileId");
        var res = await client.PostAsync($"{BaseUrl}/aml",
            new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"));

        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("SmileID AML failed {Status}: {Body}", res.StatusCode, raw);
            return false;
        }

        // AML passes if no hits found
        var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("total_count", out var count))
            return count.GetInt32() == 0;

        return true;
    }

    // ── Verify callback signature ────────────────────────────────────────────

    public bool VerifyCallbackSignature(string timestamp, string signature)
    {
        var expected = BuildSignature(timestamp);
        return expected == signature;
    }
}
