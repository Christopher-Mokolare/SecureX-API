using System.Security.Cryptography;
using System.Text;

namespace SecureX.Api.Services;

/// <summary>
/// Generates Ozow payment collection links for buyer checkout.
/// Ozow collection uses a form POST to https://pay.ozow.com with a SHA-512 hash.
/// </summary>
public class OzowCollectionService(IConfiguration config, ILogger<OzowCollectionService> logger)
{
    public record CheckoutLink(string Url, string Method, Dictionary<string, string> Fields);

    public CheckoutLink GenerateCheckoutLink(
        string dealReference,
        decimal totalAmount,
        string buyerEmail,
        string successUrl,
        string cancelUrl,
        string errorUrl,
        string notifyUrl)
    {
        var siteCode  = config["Ozow:SiteCode"]!;
        var apiKey    = config["Ozow:ApiKey"]!;
        var privateKey = config["Ozow:PrivateKey"]!;

        var amountStr = totalAmount.ToString("F2");
        var optional1 = dealReference; // echoed back in notification

        // Hash input order per Ozow collection docs:
        // SiteCode + CountryCode + CurrencyCode + Amount + TransactionReference +
        // BankReference + Optional1 + Optional2 + Optional3 + Optional4 + Optional5 +
        // IsTest + NotifyUrl + SuccessUrl + CancelUrl + ErrorUrl + ApiKey
        var isTest = config["Ozow:IsTest"] ?? "false";
        var raw = string.Concat(
            siteCode, "ZA", "ZAR", amountStr, dealReference,
            dealReference, optional1, "", "", "", "",
            isTest, notifyUrl, successUrl, cancelUrl, errorUrl, apiKey);

        var hash = Convert.ToHexString(
            SHA512.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant())))
            .ToLowerInvariant();

        var fields = new Dictionary<string, string>
        {
            ["SiteCode"]             = siteCode,
            ["CountryCode"]          = "ZA",
            ["CurrencyCode"]         = "ZAR",
            ["Amount"]               = amountStr,
            ["TransactionReference"] = dealReference,
            ["BankReference"]        = dealReference,
            ["Optional1"]            = optional1,
            ["IsTest"]               = isTest,
            ["NotifyUrl"]            = notifyUrl,
            ["SuccessUrl"]           = successUrl,
            ["CancelUrl"]            = cancelUrl,
            ["ErrorUrl"]             = errorUrl,
            ["HashCheck"]            = hash,
        };

        if (!string.IsNullOrEmpty(buyerEmail))
            fields["Customer"] = buyerEmail;

        logger.LogInformation("Generated Ozow checkout link for {Ref}", dealReference);
        return new CheckoutLink("https://pay.ozow.com", "POST", fields);
    }

    /// <summary>
    /// Verifies the SHA-512 hash on an incoming Ozow payment notification.
    /// Hash input: SiteCode + Amount + Status + TransactionReference + Optional1 + ApiKey
    /// </summary>
    public bool VerifyPaymentNotificationHash(
        string siteCode, string amount, string status,
        string transactionReference, string optional1,
        string hashCheck)
    {
        var apiKey = config["Ozow:ApiKey"]!;
        var raw = string.Concat(siteCode, amount, status, transactionReference, optional1, apiKey);
        var expected = Convert.ToHexString(
            SHA512.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant())))
            .ToLowerInvariant();
        return expected == hashCheck.ToLowerInvariant();
    }
}
