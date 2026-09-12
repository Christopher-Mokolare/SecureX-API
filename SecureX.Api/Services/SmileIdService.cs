using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Services;

public class SmileIdService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<SmileIdService> logger)
{
    public sealed record AmlResult(string JobId, string ResultCode);

    public async Task<string?> CreateBiometricKycTokenAsync()
    {
        var partnerId = config["SmileId:PartnerId"] ?? "";
        var apiKey = config["SmileId:ApiKey"] ?? "";
        var baseUrl = (config["SmileId:BaseUrl"] ?? "https://testapi.smileidentity.com").TrimEnd('/');

        if (string.IsNullOrWhiteSpace(partnerId) || string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError("SmileID credentials are not configured");
            return null;
        }

        var product = config["SmileId:BiometricProduct"] ?? "biometric_kyc";
        return await MintTokenAsync(partnerId, apiKey, baseUrl, product: product);
    }

    public async Task<string?> SubmitEnhancedKycAsync(
        string fullName, string idNumber, string email, string phone,
        string dealReference, string country = "ZA", string idType = "NATIONAL_ID")
    {
        var partnerId = config["SmileId:PartnerId"] ?? "";
        var apiKey = config["SmileId:ApiKey"] ?? "";
        var baseUrl = (config["SmileId:BaseUrl"] ?? "https://testapi.smileidentity.com").TrimEnd('/');
        var callbackUrl = config["SmileId:CallbackUrl"] ?? "";

        if (string.IsNullOrWhiteSpace(partnerId) || string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError("SmileID credentials are not configured");
            return null;
        }

        var enhancedProduct = config["SmileId:EnhancedProduct"] ?? "enhanced_kyc";
        var token = await MintTokenAsync(partnerId, apiKey, baseUrl, product: enhancedProduct, country: country);
        if (token is null) return null;

        var nameParts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nameParts.Length == 0)
        {
            logger.LogError("SmileID cannot submit KYC with an empty name for deal {Ref}", dealReference);
            return null;
        }

        var givenNames = nameParts.Length > 1 ? string.Join(' ', nameParts[..^1]) : nameParts[0];
        var lastName = nameParts.Length > 1 ? nameParts[^1] : nameParts[0];
        var cleanPhone = NormalizePhone(phone);
        var now = DateTime.UtcNow.ToString("o");
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        var fields = new List<(string Name, string Value)>
        {
            ("country", country),
            ("id_type", idType),
            ("id_number", idNumber),
            ("user_details", JsonSerializer.Serialize(new
            {
                given_names = givenNames,
                last_name = lastName,
                email,
                phone_number = cleanPhone
            }, opts)),
            ("consent", JsonSerializer.Serialize(new
            {
                granted = true,
                granted_at = now,
                notice_language = "EN",
                notice_privacy_policy_url = config["SmileId:PolicyUrl"] ?? "https://secureexchange.co.za/privacy"
            }, opts)),
            ("partner_params", JsonSerializer.Serialize(new { deal_reference = dealReference }, opts))
        };
        if (!string.IsNullOrWhiteSpace(callbackUrl))
            fields.Add(("callback_url", callbackUrl));

        using var content = CreateMultipartContent(fields.ToArray());

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/enhanced_kyc")
            {
                Content = content
            };
            request.Headers.Add("SmileID-Token", token);
            request.Headers.Add("SmileID-Partner-ID", partnerId);
            request.Headers.Add("SmileID-Source-SDK", "rest_api");
            request.Headers.Add("SmileID-Source-SDK-Version", "1.0.0");

            var response = await httpFactory.CreateClient("SmileId").SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            {
                logger.LogError("SmileID Enhanced KYC rejected {Status}: {Body}", response.StatusCode, body.Replace("\n", "").Replace("\r", ""));
                return null;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(body);
            if (result.TryGetProperty("job_id", out var jobId) &&
                !string.IsNullOrWhiteSpace(jobId.GetString()))
                return jobId.GetString();

            logger.LogError("SmileID Enhanced KYC response did not contain job_id");
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "SmileID Enhanced KYC HTTP error for deal {Ref}", dealReference);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, "SmileID Enhanced KYC request timed out for deal {Ref}", dealReference);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "SmileID Enhanced KYC returned invalid JSON for deal {Ref}", dealReference);
            return null;
        }
    }

    public bool VerifyWebhookSignature(string signature, string timestamp)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
            return false;

        var expected = BuildSignature(
            config["SmileId:PartnerId"] ?? "",
            timestamp,
            config["SmileId:ApiKey"] ?? "");

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    public async Task<AmlResult?> SubmitAmlAsync(
        string fullName, string dealReference, string country = "ZA", string? userId = null)
    {
        var partnerId = config["SmileId:PartnerId"] ?? "";
        var apiKey = config["SmileId:ApiKey"] ?? "";
        var baseUrl = (config["SmileId:BaseUrl"] ?? "https://testapi.smileidentity.com").TrimEnd('/');

        var jobId = Guid.NewGuid().ToString();
        var resolvedUserId = userId ?? Guid.NewGuid().ToString();
        var timestamp = DateTime.UtcNow.ToString("o");
        var signature = BuildSignature(partnerId, timestamp, apiKey);

        var body = new
        {
            partner_id = partnerId,
            source_sdk = "rest_api",
            source_sdk_version = "1.0.0",
            signature,
            timestamp,
            user_id = resolvedUserId,
            job_id = jobId,
            countries = new[] { country },
            full_name = fullName,
            strict_match = true,
            search_existing_user = false,
            deal_reference = dealReference,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/one-time-aml-screening")            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            var response = await httpFactory.CreateClient("SmileId").SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("SmileID AML rejected {Status}: {Body}", response.StatusCode, responseBody.Replace("\n", "").Replace("\r", ""));
                return null;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(responseBody);
            var resultCode = GetJsonString(result, "ResultCode") ?? GetJsonString(result, "result_code");
            if (string.IsNullOrWhiteSpace(resultCode))
            {
                logger.LogError("SmileID AML response did not contain ResultCode");
                return null;
            }

            logger.LogInformation("SmileID AML submitted jobId={JobId} deal={Ref} result={ResultCode}",
                jobId, dealReference, resultCode);
            return new AmlResult(jobId, resultCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "SmileID AML HTTP error for deal {Ref}", dealReference);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, "SmileID AML request timed out for deal {Ref}", dealReference);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "SmileID AML returned invalid JSON for deal {Ref}", dealReference);
            return null;
        }
    }

    private async Task<string?> MintTokenAsync(string partnerId, string apiKey, string baseUrl,
        string product, string country = "ZA")
    {
        try
        {
            logger.LogInformation("MintTokenAsync: Starting with partnerId={PartnerId}, product={Product}, country={Country}", partnerId, product, country);

            // ✅ CORRECT: SmileID /v3/token expects multipart/form-data
            var fields = new List<(string Name, string Value)>
            {
                ("partner_id", partnerId),
                ("product", product),
                ("country", country),
                ("id_type", config["SmileId:IdType"] ?? "NATIONAL_ID"),
                ("id_selection", config["SmileId:IdSelection"] ?? "false")
            };

            using var content = CreateMultipartContent(fields.ToArray());

            logger.LogInformation("MintTokenAsync: Sending request to {Url}", $"{baseUrl}/v3/token");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/token")
            {
                Content = content
            };
            request.Headers.Add("SmileID-Api-Key", apiKey);
            request.Headers.Add("SmileID-Partner-ID", partnerId);

            var response = await httpFactory.CreateClient("SmileId").SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            logger.LogInformation("MintTokenAsync: Response status={Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("SmileID token request rejected {Status}: {Body}", response.StatusCode, body.Replace("\n", "").Replace("\r", ""));
                return null;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(body);
            return result.TryGetProperty("token", out var token) && !string.IsNullOrWhiteSpace(token.GetString())
                ? token.GetString()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MintTokenAsync: Exception occurred");
            return null;
        }
    }

    private static ByteArrayContent CreateMultipartContent(params (string Name, string Value)[] fields)
    {
        const string boundary = "----SecureXSmileIdBoundary";
        var builder = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            builder.Append($"--{boundary}\r\n");
            builder.Append($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
            builder.Append(value);
            builder.Append("\r\n");
        }

        builder.Append($"--{boundary}--\r\n");
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(builder.ToString()));
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        return content;
    }

    private static string NormalizePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        clean = clean.Replace("+", string.Empty);

        if (clean.StartsWith("0") && clean.Length == 10)
            return "+27" + clean[1..];

        if (clean.StartsWith("27") && clean.Length == 11)
            return "+" + clean;

        if (clean.Length > 0)
            return "+" + clean;

        return string.Empty;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
        return null;
    }

    private static string BuildSignature(string partnerId, string timestamp, string apiKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var data = Encoding.UTF8.GetBytes($"{timestamp}{partnerId}sid_request");
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }
}