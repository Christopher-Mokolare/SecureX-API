using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Services;

public class ThisIsMeAvsService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ThisIsMeAvsService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Returns true if the bank account is verified, false otherwise.
    /// </summary>
    public async Task<bool> VerifyBankAccountAsync(
        string idNumber,
        string accountNumber,
        string branchCode,
        string accountType = "current")
    {
        var apiKey  = config["ThisIsMe:ApiKey"]!;
        var baseUrl = config["ThisIsMe:BaseUrl"] ?? "https://odin.thisisme.com/api/v1";

        var body = new
        {
            idNumber,
            bankDetails = new { accountNumber, branchCode, accountType }
        };

        var client = httpFactory.CreateClient("ThisIsMe");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/individual-avs")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("ThisIsMe AVS failed {Status}: {Body}", res.StatusCode, raw);
            return false;
        }

        var doc = JsonDocument.Parse(raw);
        // ThisIsMe returns { "verified": true/false } or { "result": { "accountVerified": true/false } }
        if (doc.RootElement.TryGetProperty("verified", out var v))
            return v.GetBoolean();
        if (doc.RootElement.TryGetProperty("result", out var r) &&
            r.TryGetProperty("accountVerified", out var av))
            return av.GetBoolean();

        logger.LogWarning("ThisIsMe AVS unexpected response shape: {Body}", raw);
        return false;
    }
}
