using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureX.Api.Services;

public class OzowPayoutService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<OzowPayoutService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Public entry point ───────────────────────────────────────────────────

    public async Task<string?> RequestPayoutAsync(
        string merchantReference,
        decimal amountZar,
        string bankGroupId,
        string plainAccountNumber,
        string branchCode,
        string encryptionKey,
        string notifyUrl)
    {
        var siteCode  = config["Ozow:SiteCode"]!;
        var apiKey    = config["Ozow:PayoutApiKey"]!;
        var baseUrl   = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";

        var amountCents = (long)Math.Round(amountZar * 100);
        var encryptedAccount = EncryptAccountNumber(plainAccountNumber, merchantReference, amountCents, encryptionKey);
        var customerRef = SanitiseBankRef(merchantReference);

        var hash = BuildHash(siteCode, amountZar, merchantReference, customerRef,
            false, notifyUrl, bankGroupId, encryptedAccount, branchCode, apiKey);

        var body = new
        {
            siteCode,
            amount        = amountZar,
            merchantReference,
            customerBankReference = customerRef,
            isRtc         = false,
            notifyUrl,
            bankingDetails = new { bankGroupId, accountNumber = encryptedAccount, branchCode },
            hashCheck     = hash,
        };

        var client = httpFactory.CreateClient("OzowPayout");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/requestpayout")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("SiteCode", siteCode);
        req.Headers.Add("ApiKey", apiKey);

        var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("Ozow requestpayout failed {Status}: {Body}", res.StatusCode, raw);
            return null;
        }

        var doc = JsonDocument.Parse(raw);
        var payoutId = doc.RootElement.GetProperty("payoutId").GetString();
        logger.LogInformation("Ozow payout submitted. PayoutId={PayoutId} Ref={Ref}", payoutId, merchantReference);
        return payoutId;
    }

    // ── AES-256-CBC account number encryption ────────────────────────────────
    // IV = first 16 chars of SHA512(merchantReference + amountCents + encryptionKey) as UTF8 bytes
    // Key = first 32 chars of encryptionKey (padded if shorter) as UTF8 bytes
    // Output = Base64

    private static string EncryptAccountNumber(string accountNumber, string merchantReference, long amountCents, string encryptionKey)
    {
        var ivInput = $"{merchantReference}{amountCents}{encryptionKey}";
        var ivHex   = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(ivInput.ToLowerInvariant()))).ToLowerInvariant();
        var iv      = Encoding.UTF8.GetBytes(ivHex[..16]);

        // Pad key to 32 bytes using string repetition per Ozow docs
        var k = encryptionKey;
        while (k.Length < 32) k += k;
        var key = Encoding.UTF8.GetBytes(k[..32]);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key     = key;
        aes.IV      = iv;

        using var enc = aes.CreateEncryptor();
        var plain  = Encoding.UTF8.GetBytes(accountNumber);
        var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
        return Convert.ToBase64String(cipher);
    }

    // ── SHA-512 hash per Ozow docs ───────────────────────────────────────────
    // siteCode + amountCents + merchantReference + customerBankReference +
    // isRtc + notifyUrl + bankGroupId + accountNumber + branchCode + apiKey

    private static string BuildHash(
        string siteCode, decimal amount, string merchantRef, string customerRef,
        bool isRtc, string notifyUrl, string bankGroupId, string accountNumber,
        string branchCode, string apiKey)
    {
        var amountCents = (long)Math.Round(amount * 100);
        var raw = string.Concat(
            siteCode, amountCents, merchantRef, customerRef,
            isRtc.ToString().ToLowerInvariant(), notifyUrl,
            bankGroupId, accountNumber, branchCode, apiKey);

        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Bank ref: alphanumeric + spaces + dashes only, max 20 chars
    private static string SanitiseBankRef(string input) =>
        new string(input.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray())[..Math.Min(input.Length, 20)];
}
