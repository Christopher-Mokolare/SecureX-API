using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SecureX.Api.Services;

public class SmileIdService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<SmileIdService> logger)
{

    // Submits an Enhanced KYC job. Returns the job_id on success, null on failure.
    public async Task<string?> SubmitEnhancedKycAsync(
        string fullName, string idNumber, string email, string phone,
        string dealReference, string country = "ZA", string idType = "NATIONAL_ID")
    {
        var partnerId  = config["SmileId:PartnerId"] ?? "";
        var apiKey     = config["SmileId:ApiKey"]    ?? "";
        var baseUrl    = config["SmileId:BaseUrl"]   ?? "https://api.sandbox.smileidentity.com";
        var callbackUrl = config["SmileId:CallbackUrl"] ?? config["SMILEID_CALLBACK_URL"] ?? "";

        var token = await MintTokenAsync(partnerId, apiKey, baseUrl);
        if (token is null)
        {
            logger.LogError("SmileID: failed to mint token for deal {Ref}", dealReference);
            return null;
        }

        var nameParts  = fullName.Trim().Split(' ');
        var givenNames = string.Join(' ', nameParts[..^1]);
        var lastName   = nameParts[^1];

        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var now = DateTime.UtcNow.ToString("o");

        // curl -F sends parts with no Content-Type header; StringContent adds "text/plain; charset=utf-8"
        // which SmileID rejects. Use ByteArrayContent to send raw bytes with no content-type.
        static ByteArrayContent Field(string value)
        {
            var c = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
            c.Headers.ContentType = null;
            return c;
        }

        var form = new MultipartFormDataContent();
        form.Add(Field(country),     "country");
        form.Add(Field(idType),      "id_type");
        form.Add(Field(idNumber),    "id_number");
        form.Add(Field(callbackUrl), "callback_url");
        form.Add(Field(JsonSerializer.Serialize(new
        {
            given_names  = givenNames,
            last_name    = lastName,
            email,
            phone_number = phone
        }, opts)), "user_details");
        form.Add(Field(JsonSerializer.Serialize(new
        {
            granted    = true,
            granted_at = now,
            notice_language = "en",
            notice_privacy_policy_url = config["SmileId:PolicyUrl"] ?? "https://secureexchange.co.za/privacy"
        }, opts)), "consent");
        form.Add(Field(JsonSerializer.Serialize(new { deal_reference = dealReference }, opts)), "partner_params");

        var client = httpFactory.CreateClient("SmileId");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/enhanced_kyc") { Content = form };
            request.Headers.Add("SmileID-Token", token);
            var resp    = await client.SendAsync(request);
            var content = await resp.Content.ReadAsStringAsync();

            if ((int)resp.StatusCode != 202)
            {
                logger.LogError("SmileID Enhanced KYC rejected {Status}: {Body}", resp.StatusCode, content);
                return null;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(content);
            if (result.TryGetProperty("job_id", out var jobId))
                return jobId.GetString();

            logger.LogError("SmileID Enhanced KYC: no job_id in 202 response: {Body}", content);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SmileID Enhanced KYC HTTP error for deal {Ref}", dealReference);
            return null;
        }
    }

    // Verifies an incoming webhook signature.
    // HMAC-SHA256 of (timestamp + partnerId + "sid_request") using apiKey.
    public bool VerifyWebhookSignature(string signature, string timestamp)
    {
        var partnerId = config["SmileId:PartnerId"] ?? "";
        var apiKey    = config["SmileId:ApiKey"]    ?? "";
        var expected  = BuildSignature(partnerId, timestamp, apiKey);
        return string.Equals(expected, signature, StringComparison.Ordinal);
    }

    private async Task<string?> MintTokenAsync(string partnerId, string apiKey, string baseUrl)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        var signature = BuildSignature(partnerId, timestamp, apiKey);

        try
        {
            static ByteArrayContent Field(string value)
            {
                var c = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
                c.Headers.ContentType = null;
                return c;
            }
            var client  = httpFactory.CreateClient("SmileId");
            var content = new MultipartFormDataContent();
            content.Add(Field(partnerId), "partner_id");
            content.Add(Field(timestamp), "timestamp");
            content.Add(Field(signature), "signature");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/token") { Content = content };
            request.Headers.Add("smileid-api-key", apiKey);
            request.Headers.Add("smileid-partner-id", partnerId);
            var resp       = await client.SendAsync(request);
            var contentStr = await resp.Content.ReadAsStringAsync();
            var result     = JsonSerializer.Deserialize<JsonElement>(contentStr);
            if (!result.TryGetProperty("token", out var t))
            {
                logger.LogError("SmileID: token response {Status}: {Body}", resp.StatusCode, contentStr);
                return null;
            }
            return t.GetString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SmileID: token mint failed");
            return null;
        }
    }

    private static string BuildSignature(string partnerId, string timestamp, string apiKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var data = Encoding.UTF8.GetBytes($"{timestamp}{partnerId}sid_request");
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }
}
