using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Services;

/// <summary>
/// Ozow One API — OAuth 2.0 client credentials flow + REST payment creation.
/// Docs: https://hub.ozow.com/docs/one-api/quickstart-payments
/// </summary>
public class OzowCollectionService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<OzowCollectionService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null }; // Ozow One API expects PascalCase

    private string BaseUrl    => config["Ozow:OneApiBaseUrl"] ?? "https://stagingone.ozow.com";
    private string ClientId   => config["Ozow:OneApiClientId"]!;
    private string ClientSecret => config["Ozow:OneApiClientSecret"]!;
    private string SiteCode   => config["Ozow:SiteCode"]!;
    private string ReturnUrl  => config["Ozow:ReturnUrl"] ?? "http://securex-alb-1751040376.af-south-1.elb.amazonaws.com/payment-return";
    private string NotifyUrl  => config["Ozow:CollectionNotifyUrl"] ?? "http://securex-alb-1751040376.af-south-1.elb.amazonaws.com/securex/payment-notification";

    // ── Step 1: Get OAuth access token ───────────────────────────────────────

    private async Task<string?> GetAccessTokenAsync()
    {
        var client = httpFactory.CreateClient("OzowOneApi");
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = ClientId,
            ["client_secret"] = ClientSecret,
            ["scope"]         = "payments",
            ["grant_type"]    = "client_credentials",
        });

        var res = await client.PostAsync($"{BaseUrl}/v1/token", body);
        var raw = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("Ozow One API token failed {Status}: {Body}", res.StatusCode, raw);
            return null;
        }

        var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    // ── Step 2: Create payment → returns redirectUrl ──────────────────────────

    public async Task<string?> CreatePaymentAsync(string dealReference, decimal totalAmount)
    {
        var token = await GetAccessTokenAsync();
        if (token is null) return null;

        var body = new
        {
            SiteCode          = SiteCode,
            Amount            = totalAmount,
            CurrencyCode      = "ZAR",
            MerchantReference = dealReference,
            BankReference     = dealReference,
            ExpireAt          = DateTime.UtcNow.AddHours(24).ToString("o"),
            NotifyUrl         = NotifyUrl,
            ReturnUrl         = ReturnUrl,
            IsTest            = true,
        };

        var client = httpFactory.CreateClient("OzowOneApi");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/payments")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("Ozow One API create payment failed {Status}: {Body}", res.StatusCode, raw);
            return null;
        }

        var doc = JsonDocument.Parse(raw);
        var redirectUrl = doc.RootElement.GetProperty("redirectUrl").GetString();
        logger.LogInformation("Ozow One API payment created. Ref={Ref} RedirectUrl={Url}", dealReference, redirectUrl);
        return redirectUrl;
    }
}
