using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class HashService
{
    private static byte[] Sha512Bytes(string input) =>
        SHA512.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));

    private static string Sha512Lower(string input) =>
        Convert.ToHexString(Sha512Bytes(input)).ToLowerInvariant();

    // Timing-safe comparison — prevents hash oracle timing attacks
    private static bool FixedTimeEqual(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    // ── Verify payout-verify webhook ─────────────────────────────────────────

    public bool VerifyPayoutHash(PayoutVerifyRequest req, string apiKey)
    {
        var cents = (long)Math.Round(req.Amount * 100);

        var input = string.Concat(
            req.PayoutId,
            req.SiteCode,
            cents,
            req.MerchantReference,
            req.CustomerBankReference,
            req.IsRtc.ToString().ToLowerInvariant(),
            req.NotifyUrl,
            req.BankingDetails?.BankGroupId,
            req.BankingDetails?.AccountNumber,
            req.BankingDetails?.BranchCode,
            apiKey);

        return FixedTimeEqual(
            Sha512Lower(input),
            req.HashCheck.ToLowerInvariant());
    }

    // ── Verify payout-notification webhook ───────────────────────────────────

    public bool VerifyNotificationHash(
        PayoutNotificationRequest req,
        string apiKey,
        out int status,
        out int subStatus)
    {
        (status, subStatus) = ReadStatus(req);

        var input = string.Concat(
            req.PayoutId,
            req.SiteCode,
            req.MerchantReference,
            req.CustomerMerchantReference,
            status,
            subStatus,
            apiKey);

        return FixedTimeEqual(
            Sha512Lower(input),
            req.HashCheck.ToLowerInvariant());
    }

    // ── Verify Payments API payment-notification webhook ─────────────────────
    //
    // Ozow Payments API notification hash:
    //
    // SiteCode
    // TransactionId
    // TransactionReference
    // Amount (exactly two decimals)
    // Status
    // Optional1
    // Optional2
    // Optional3
    // Optional4
    // Optional5
    // CurrencyCode
    // IsTest
    // StatusMessage
    // PrivateKey
    //
    // Empty optional fields are omitted.

    public bool VerifyPaymentNotificationHash(
        string siteCode,
        string transactionId,
        string transactionReference,
        string amount,
        string status,
        string? optional1,
        string? optional2,
        string? optional3,
        string? optional4,
        string? optional5,
        string currencyCode,
        string isTest,
        string? statusMessage,
        string privateKey,
        string hashCheck)
    {
        if (!decimal.TryParse(
                amount,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedAmount))
        {
            return false;
        }

        var normalizedAmount = parsedAmount.ToString(
            "0.00",
            CultureInfo.InvariantCulture);

        var input = string.Concat(
            siteCode,
            transactionId,
            transactionReference,
            normalizedAmount,
            status,
            optional1 ?? "",
            optional2 ?? "",
            optional3 ?? "",
            optional4 ?? "",
            optional5 ?? "",
            currencyCode,
            isTest,
            statusMessage ?? "",
            privateKey);

        return FixedTimeEqual(
            Sha512Lower(input),
            hashCheck.Trim().ToLowerInvariant());
    }

    public static (int status, int subStatus) ReadStatus(
        PayoutNotificationRequest req)
    {
        int s = 0;
        int ss = req.PayoutSubStatus ?? req.SubStatus ?? 0;

        if (req.PayoutStatus is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                s = el.GetInt32();
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(
                        el,
                        "status",
                        out var sp) &&
                    sp.ValueKind == JsonValueKind.Number)
                {
                    s = sp.GetInt32();
                }

                if (TryGetPropertyIgnoreCase(
                        el,
                        "subStatus",
                        out var ssp) &&
                    ssp.ValueKind == JsonValueKind.Number)
                {
                    ss = ssp.GetInt32();
                }
            }
        }

        return (s, ss);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
