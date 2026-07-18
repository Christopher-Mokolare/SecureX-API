using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Services;

/// <summary>
/// Ozow standard payment request API.
/// Docs: https://hub.ozow.com/docs/payment-request
/// POST https://stagingapi.ozow.com/PostPaymentRequest
/// </summary>
public class OzowCollectionService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<OzowCollectionService> logger)
{
    private string BaseUrl     => config["Ozow:CollectionBaseUrl"] ?? "https://stagingapi.ozow.com";
    private string SiteCode    => config["Ozow:SiteCode"]!;
    private string PrivateKey  => config["Ozow:PrivateKey"]!;
    private string ApiKey      => config["Ozow:ApiKey"]!;
    private string NotifyUrl   => config["Ozow:CollectionNotifyUrl"]!;
    private string ReturnUrl   => config["Ozow:ReturnUrl"] ?? "http://securex-alb-1751040376.af-south-1.elb.amazonaws.com/payment-return";

    public async Task<string?> CreatePaymentAsync(string dealReference, decimal totalAmount)
    {
        var countryCode  = "ZA";
        var currencyCode = "ZAR";
        var amount       = totalAmount.ToString("F2");
        var bankRef      = SanitiseRef(dealReference);
        var optional1    = "";
        var optional2    = "";
        var optional3    = "";
        var isTest       = "true";
        var cancelUrl    = ReturnUrl;
        var errorUrl     = ReturnUrl;

        var hash = BuildHash(SiteCode, countryCode, currencyCode, amount,
            bankRef, optional1, optional2, optional3,
            cancelUrl, errorUrl, NotifyUrl, ReturnUrl, isTest, PrivateKey);

        var body = new
        {
            SiteCode    = SiteCode,
            CountryCode = countryCode,
            CurrencyCode = currencyCode,
            Amount      = amount,
            TransactionReference = dealReference,
            BankReference = bankRef,
            Optional1   = optional1,
            Optional2   = optional2,
            Optional3   = optional3,
            CancelUrl   = cancelUrl,
            ErrorUrl    = errorUrl,
            SuccessUrl  = ReturnUrl,
            NotifyUrl   = NotifyUrl,
            IsTest      = isTest,
            HashCheck   = hash,
        };

        var client = httpFactory.CreateClient("OzowCollection");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/PostPaymentRequest")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("ApiKey", ApiKey);

        var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("Ozow PostPaymentRequest failed {Status}: {Body}", res.StatusCode, raw);
            return null;
        }

        var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("url", out var urlProp))
        {
            logger.LogError("Ozow PostPaymentRequest response missing 'url': {Body}", raw);
            return null;
        }

        var url = urlProp.GetString();
        logger.LogInformation("Ozow payment created. Ref={Ref} Url={Url}", dealReference, url);
        return url;
    }

    // SHA-512 hash per Ozow docs:
    // siteCode + countryCode + currencyCode + amount + transactionRef + bankRef +
    // optional1 + optional2 + optional3 + cancelUrl + errorUrl + successUrl + notifyUrl + isTest + privateKey
    private static string BuildHash(
        string siteCode, string countryCode, string currencyCode, string amount,
        string bankRef, string opt1, string opt2, string opt3,
        string cancelUrl, string errorUrl, string notifyUrl, string successUrl,
        string isTest, string privateKey)
    {
        var input = string.Concat(
            siteCode, countryCode, currencyCode, amount,
            bankRef, opt1, opt2, opt3,
            cancelUrl, errorUrl, successUrl, notifyUrl, isTest, privateKey);

        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitiseRef(string input) =>
        new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray())[..Math.Min(input.Length, 20)];
}
