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

    public sealed record OzowPayoutLookup(
    string PayoutId,
    decimal Amount,
    int Status,
    int SubStatus,
    string? ErrorMessage);

public async Task<string?> RequestPayoutAsync(
        string merchantReference,
        decimal amountZar,
        string bankGroupId,
        string plainAccountNumber,
        string branchCode,
        string encryptionKey,
        string notifyUrl,
        string verifyUrl)
    {
        var siteCode  = config["Ozow:SiteCode"]!;
        var apiKey    = config["Ozow:PayoutApiKey"]!;
        var baseUrl   = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";

        var amountCents = (long)Math.Round(amountZar * 100);
        var isRtc = bool.TryParse(config["Ozow:PayoutIsRtc"], out var rtc) && rtc;
        var encryptedAccount = EncryptAccountNumber(plainAccountNumber, merchantReference, amountCents, encryptionKey);
        var customerRef = SanitiseBankRef(merchantReference);

        var hash = BuildHash(siteCode, amountCents, merchantReference, customerRef,
            isRtc, notifyUrl, bankGroupId, encryptedAccount, branchCode, apiKey);

        var body = new
        {
            siteCode,
            amount        = amountZar,
            merchantReference,
            customerBankReference = customerRef,
            isRtc,
            notifyUrl,
            verifyUrl,
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
        var payoutId = doc.RootElement.TryGetProperty("payoutId", out var pid) ? pid.GetString() : null;
        var payoutStatus = doc.RootElement.TryGetProperty("payoutStatus", out var ps) ? ps : default;
        var status = payoutStatus.ValueKind == JsonValueKind.Object &&
                     payoutStatus.TryGetProperty("status", out var statusEl) &&
                     statusEl.ValueKind == JsonValueKind.Number
            ? statusEl.GetInt32()
            : 0;
        var subStatus = payoutStatus.ValueKind == JsonValueKind.Object &&
                        payoutStatus.TryGetProperty("subStatus", out var subStatusEl) &&
                        subStatusEl.ValueKind == JsonValueKind.Number
            ? subStatusEl.GetInt32()
            : 0;
        var errorMessage = payoutStatus.ValueKind == JsonValueKind.Object &&
                           payoutStatus.TryGetProperty("errorMessage", out var errorEl)
            ? errorEl.GetString()
            : null;

        // Ozow can return HTTP 200 for a rejected payout. Only a payoutId means
        // the payout was accepted into the payout workflow.
        if (string.IsNullOrEmpty(payoutId))
        {
            logger.LogError(
                "Ozow requestpayout rejected. Ref={Ref} status={Status} subStatus={SubStatus} error={Error}",
                merchantReference, status, subStatus, errorMessage);
            return null;
        }

        logger.LogInformation("Ozow payout submitted. PayoutId={PayoutId} Ref={Ref}",
            payoutId.Replace("\n", "").Replace("\r", ""),
            merchantReference.Replace("\n", "").Replace("\r", ""));
        return payoutId;
    }

    /// <summary>
    /// Recovers an already-created Ozow payout when the provider accepted a request
    /// but the local database write failed. Ozow documents this endpoint specifically
    /// for status checks by merchant reference.
    /// </summary>
    public async Task<OzowPayoutLookup?> FindExistingPayoutAsync(string merchantReference, decimal amountZar, CancellationToken cancellationToken = default)
    {
        var siteCode = config["Ozow:SiteCode"] ?? throw new InvalidOperationException("Ozow SiteCode is not configured");
        var apiKey = config["Ozow:PayoutApiKey"] ?? throw new InvalidOperationException("Ozow payout API key is not configured");
        var baseUrl = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";

        var client = httpFactory.CreateClient("OzowPayout");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/getpayoutbyreference")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                pageSize = 10,
                pageIndex = 1,
                searchFields = new[] { "0" },
                searchString = merchantReference,
                minAmount = amountZar,
                maxAmount = amountZar,
            }, JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("SiteCode", siteCode);
        req.Headers.Add("ApiKey", apiKey);

        var res = await client.SendAsync(req, cancellationToken);
        var raw = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("Ozow payout lookup failed {Status} for Ref={Ref}: {Body}", res.StatusCode, merchantReference, raw);
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var payout in doc.RootElement.EnumerateArray())
        {
            var reference = payout.TryGetProperty("merchantReference", out var mr) ? mr.GetString() : null;
            var amount = payout.TryGetProperty("amount", out var av) && av.ValueKind == JsonValueKind.Number
                ? av.GetDecimal()
                : (decimal?)null;
            var id = payout.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var payoutStatus = payout.TryGetProperty("payoutStatus", out var ps) ? ps : default;
            var status = payoutStatus.ValueKind == JsonValueKind.Object &&
                         payoutStatus.TryGetProperty("status", out var statusEl) &&
                         statusEl.ValueKind == JsonValueKind.Number
                ? statusEl.GetInt32()
                : 0;
            var subStatus = payoutStatus.ValueKind == JsonValueKind.Object &&
                            payoutStatus.TryGetProperty("subStatus", out var subStatusEl) &&
                            subStatusEl.ValueKind == JsonValueKind.Number
                ? subStatusEl.GetInt32()
                : 0;
            var errorMessage = payoutStatus.ValueKind == JsonValueKind.Object &&
                               payoutStatus.TryGetProperty("errorMessage", out var errorEl)
                ? errorEl.GetString()
                : null;

            if (string.Equals(reference, merchantReference, StringComparison.Ordinal) &&
                amount.HasValue && amount.Value == amountZar &&
                !string.IsNullOrWhiteSpace(id))
            {
                logger.LogWarning(
                    "Found existing Ozow payout {PayoutId} for Ref={Ref}; status={Status} subStatus={SubStatus}",
                    id, merchantReference, status, subStatus);
                return new OzowPayoutLookup(id, amount.Value, status, subStatus, errorMessage);
            }
        }

        return null;
    }

    // Backward-compatible ID-only lookup for callers that only need existence.
    public async Task<string?> FindExistingPayoutIdAsync(string merchantReference, decimal amountZar, CancellationToken cancellationToken = default)
    {
        var payout = await FindExistingPayoutAsync(merchantReference, amountZar, cancellationToken);
        return payout?.PayoutId;
    }

    // ── AES-256-CBC account number encryption ────────────────────────────────
    // Ozow's payout-verify endpoint requires AES-256-CBC with a specific IV derivation.
    // CBC is mandated by the Ozow spec here — do not change the cipher mode.
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

#pragma warning disable CA5358 // Ozow payout-verify spec mandates AES-CBC — cannot use GCM here
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
#pragma warning restore CA5358
    }

    // ── SHA-512 hash per Ozow docs ───────────────────────────────────────────
    // siteCode + amountCents + merchantReference + customerBankReference +
    // isRtc + notifyUrl + bankGroupId + accountNumber + branchCode + apiKey

    private static string BuildHash(
        string siteCode, long amountCents, string merchantRef, string customerRef,
        bool isRtc, string notifyUrl, string bankGroupId, string accountNumber,
        string branchCode, string apiKey)
    {
        var raw = string.Concat(
            siteCode, amountCents, merchantRef, customerRef,
            isRtc.ToString().ToLowerInvariant(), notifyUrl,
            bankGroupId, accountNumber, branchCode, apiKey);

        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Bank ref: alphanumeric + spaces + dashes only, max 20 chars
    private static string SanitiseBankRef(string input) =>
        new string(input.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').Take(20).ToArray());
}
