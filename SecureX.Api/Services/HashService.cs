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
            req.PayoutId, req.SiteCode, cents,
            req.MerchantReference, req.CustomerBankReference,
            req.IsRtc.ToString().ToLowerInvariant(), req.NotifyUrl,
            req.BankingDetails?.BankGroupId, req.BankingDetails?.AccountNumber,
            req.BankingDetails?.BranchCode, apiKey);

        return FixedTimeEqual(Sha512Lower(input), req.HashCheck.ToLowerInvariant());
    }

    // ── Verify payout-notification webhook ───────────────────────────────────

    public bool VerifyNotificationHash(PayoutNotificationRequest req, string apiKey,
        out int status, out int subStatus)
    {
        (status, subStatus) = ReadStatus(req);

        var input = string.Concat(
            req.PayoutId, req.SiteCode,
            req.MerchantReference, req.CustomerMerchantReference,
            status, subStatus, apiKey);

        return FixedTimeEqual(Sha512Lower(input), req.HashCheck.ToLowerInvariant());
    }

    // ── Verify One API payment-notification webhook ───────────────────────────
    // Ozow One API signs the notification with SHA-512(siteCode + merchantReference + status + clientSecret)

    public bool VerifyPaymentNotificationHash(string siteCode, string merchantReference,
        string status, string clientSecret, string hashCheck)
    {
        var input = string.Concat(siteCode, merchantReference, status, clientSecret);
        return FixedTimeEqual(Sha512Lower(input), hashCheck.ToLowerInvariant());
    }

    public static (int status, int subStatus) ReadStatus(PayoutNotificationRequest req)
    {
        int s = 0;
        if (req.PayoutStatus is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number) s = el.GetInt32();
            else if (el.ValueKind == JsonValueKind.Object)
                s = el.TryGetProperty("status", out var sp) ? sp.GetInt32() : 0;
            // string like "Complete" — leave as 0, hash will fail and be rejected
        }
        var ss = req.PayoutSubStatus ?? req.SubStatus ?? 0;
        return (s, ss);
    }
}
