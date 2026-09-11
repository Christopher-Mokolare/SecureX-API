using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Services;

/// <summary>
/// Ozow standard payment request API.
/// POST https://stagingapi.ozow.com/PostPaymentRequest
/// Docs: https://hub.ozow.com/docs/payment-request
/// </summary>
public class OzowCollectionService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<OzowCollectionService> logger)
{
    private string BaseUrl    => config["Ozow:CollectionBaseUrl"] ?? "https://stagingapi.ozow.com";
    private string SiteCode   => config["Ozow:SiteCode"]!;
    private string PrivateKey => config["Ozow:PrivateKey"]!;
    private string ApiKey     => config["Ozow:ApiKey"]!;
    private string NotifyUrl  => config["Ozow:CollectionNotifyUrl"]!;
    private string ReturnUrl  => config["Ozow:ReturnUrl"] ?? "https://secureexchange.co.za/payment-return";

    private string BankRefPrefix => config["Ozow:BankRefPrefix"] ?? "";

    public async Task<string?> CreatePaymentAsync(string dealReference, decimal totalAmount,
        string? returnUrl = null, string? optional1 = null, string? optional2 = null, string? optional3 = null)
    {
        var amount   = totalAmount.ToString("F2");
        var bankRef  = SanitiseRef(BankRefPrefix + dealReference);
        var isTest   = config["Ozow:IsTest"] ?? "true";
        var opt1 = optional1 ?? "";
        var opt2 = optional2 ?? "";
        var opt3 = optional3 ?? "";
        var effectiveReturnUrl = returnUrl ?? ReturnUrl;

        var hash = BuildHash(SiteCode, "ZA", "ZAR", amount, dealReference, bankRef,
            opt1, opt2, opt3, effectiveReturnUrl, effectiveReturnUrl, effectiveReturnUrl, NotifyUrl, isTest, PrivateKey);

        var body = new
        {
            SiteCode             = SiteCode,
            CountryCode          = "ZA",
            CurrencyCode         = "ZAR",
            Amount               = amount,
            TransactionReference = dealReference,
            BankReference        = bankRef,
            Optional1            = opt1,
            Optional2            = opt2,
            Optional3            = opt3,
            CancelUrl             = effectiveReturnUrl,
            ErrorUrl              = effectiveReturnUrl,
            SuccessUrl            = effectiveReturnUrl,
            NotifyUrl            = NotifyUrl,
            IsTest               = isTest,
            HashCheck            = hash,
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
            logger.LogError("Ozow PostPaymentRequest missing 'url': {Body}", raw);
            return null;
        }

        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url))
        {
            logger.LogError("Ozow returned null url. Raw={Raw}", raw);
            return null;
        }
        logger.LogInformation("Ozow payment created. Ref={Ref} Url={Url}",
            dealReference.Replace("\n", "").Replace("\r", ""),
            url.Replace("\n", "").Replace("\r", ""));
        return url;
    }

    // SHA-512: siteCode+countryCode+currencyCode+amount+transactionRef+bankRef+opt1+opt2+opt3+cancelUrl+errorUrl+successUrl+notifyUrl+isTest+privateKey
    private static string BuildHash(string siteCode, string country, string currency, string amount,
        string transactionRef, string bankRef, string opt1, string opt2, string opt3,
        string cancelUrl, string errorUrl, string successUrl, string notifyUrl,
        string isTest, string privateKey)
    {
        var input = string.Concat(siteCode, country, currency, amount,
            transactionRef, bankRef, opt1, opt2, opt3, cancelUrl, errorUrl, successUrl, notifyUrl, isTest, privateKey);
        return Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()))).ToLowerInvariant();
    }

    private static string SanitiseRef(string input) =>
        new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-').Take(20).ToArray());


    public record OzowBank(string BankGroupId, string BankName, string BranchCode);

    public async Task<List<OzowBank>> GetBanksAsync()
    {
        // Hardcoded: Ozow bank list lives on the payouts API.
        // The collection API (api.ozow.com) does NOT expose this endpoint.
        const string baseUrl = "https://payoutsapi.ozow.com";
        var url = $"{baseUrl}/v1/getavailablebanks";

        var client = httpFactory.CreateClient("OzowCollection");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("SiteCode", SiteCode);
        req.Headers.Add("ApiKey", config["Ozow:PayoutApiKey"] ?? ApiKey);
        req.Headers.Add("Accept", "application/json");

        try
        {
            var res = await client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Ozow banks API returned {Status} for {Url}", res.StatusCode, url);
                return new List<OzowBank>();
            }

            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var list = new List<OzowBank>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var bankGroupId = GetString(item, "bankGroupId") ?? GetString(item, "BankGroupId") ?? "";
                var bankName = GetString(item, "bankGroupName") ?? GetString(item, "BankGroupName") ?? "";
                var branchCode = GetString(item, "universalBranchCode") ?? GetString(item, "UniversalBranchCode") ?? "";

                if (!string.IsNullOrWhiteSpace(bankGroupId) && !string.IsNullOrWhiteSpace(bankName))
                {
                    list.Add(new OzowBank(bankGroupId, bankName, branchCode));
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch Ozow banks");
            return new List<OzowBank>();
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
            }
        }
        return null;
    }
}
