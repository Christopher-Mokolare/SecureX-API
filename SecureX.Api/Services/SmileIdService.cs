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
        var partnerId = config["SmileId:PartnerId"] ?? "";
        var apiKey = config["SmileId:ApiKey"] ?? "";
        var baseUrl = config["SmileId:BaseUrl"] ?? "https://api.sandbox.smileidentity.com";
        var callbackUrl = config["SmileId:CallbackUrl"] ?? config["SMILEID_CALLBACK_URL"] ?? "";

        var token = await MintTokenAsync(partnerId, apiKey, baseUrl);
        if (token is null)
        {
            logger.LogError("SmileID: failed to mint token for deal {Ref}", dealReference);
            return null;
        }

        logger.LogInformation("SmileID: callbackUrl={CallbackUrl}", callbackUrl);

        var nameParts = fullName.Trim().Split(' ');
        var givenNames = string.Join(' ', nameParts[..^1]);
        var lastName = nameParts[^1];

        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var now = DateTime.UtcNow.ToString("o");

        var cleanPhone = phone;
        if (!string.IsNullOrEmpty(cleanPhone))
        {
            cleanPhone = new string(cleanPhone.Where(c => char.IsDigit(c) || c == '+').ToArray());
            if (cleanPhone.StartsWith("0"))
                cleanPhone = "+27" + cleanPhone[1..];
            else if (!cleanPhone.StartsWith("+"))
                cleanPhone = "+" + cleanPhone;
        }

        var userDetails = JsonSerializer.Serialize(new { given_names = givenNames, last_name = lastName, email, phone_number = cleanPhone }, opts);
        var consent = JsonSerializer.Serialize(new { granted = true, granted_at = now, notice_language = "en", notice_privacy_policy_url = config["SmileId:PolicyUrl"] ?? "https://secureexchange.co.za/privacy" }, opts);
        var partnerParams = JsonSerializer.Serialize(new { deal_reference = dealReference }, opts);

        // Build raw multipart body manually — C# MultipartFormDataContent quotes the boundary
        // (boundary="abc") but SmileID requires unquoted (boundary=abc), matching curl -F behavior.
        var boundary = "----SmileIDBoundary";
        var sb = new StringBuilder();
        void AddField(string name, string value)
        {
            sb.Append($"--{boundary}\r\n");
            sb.Append($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
            sb.Append(value);
            sb.Append("\r\n");
        }
        AddField("country", country);
        AddField("id_type", idType);
        AddField("id_number", idNumber);
        AddField("callback_url", callbackUrl);
        AddField("user_details", userDetails);
        AddField("consent", consent);
        AddField("partner_params", partnerParams);
        sb.Append($"--{boundary}--\r\n");

        var bodyBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var rawContent = new ByteArrayContent(bodyBytes);
        rawContent.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        var client = httpFactory.CreateClient("SmileId");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/enhanced_kyc") { Content = rawContent };
            request.Headers.Add("SmileID-Token", token);
            var resp = await client.SendAsync(request);
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
        var apiKey = config["SmileId:ApiKey"] ?? "";
        var expected = BuildSignature(partnerId, timestamp, apiKey);
        return string.Equals(expected, signature, StringComparison.Ordinal);
    }

    private async Task<string?> MintTokenAsync(string partnerId, string apiKey, string baseUrl)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        var signature = BuildSignature(partnerId, timestamp, apiKey);

        try
        {
            var boundary = "----SmileIDBoundary";
            var sb = new StringBuilder();
            void AddField(string name, string value) { sb.Append($"--{boundary}\r\n"); sb.Append($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n"); sb.Append(value); sb.Append("\r\n"); }
            AddField("partner_id", partnerId);
            AddField("timestamp", timestamp);
            AddField("signature", signature);
            sb.Append($"--{boundary}--\r\n");
            var bodyBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var content = new ByteArrayContent(bodyBytes);
            content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
            var client = httpFactory.CreateClient("SmileId");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/token") { Content = content };
            request.Headers.Add("smileid-api-key", apiKey);
            request.Headers.Add("smileid-partner-id", partnerId);
            var resp = await client.SendAsync(request);
            var contentStr = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(contentStr);
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