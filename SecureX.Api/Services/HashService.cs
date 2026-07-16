using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class HashService
{
    private static string Sha512Lower(string input) =>
        Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()))).ToLowerInvariant();

    // ── Verify webhook ───────────────────────────────────────────────────────

    public bool VerifyPayoutHash(PayoutVerifyRequest req, string apiKey)
    {
        var cents = (long)Math.Round(req.Amount * 100);
        var input = string.Concat(
            req.PayoutId, req.SiteCode, cents,
            req.MerchantReference, req.CustomerBankReference,
            req.IsRtc.ToString().ToLowerInvariant(), req.NotifyUrl,
            req.BankingDetails?.BankGroupId, req.BankingDetails?.AccountNumber,
            req.BankingDetails?.BranchCode, apiKey);

        return Sha512Lower(input) == req.HashCheck.ToLowerInvariant();
    }

    // ── Notification webhook ─────────────────────────────────────────────────

    public bool VerifyNotificationHash(PayoutNotificationRequest req, string apiKey,
        out int status, out int subStatus)
    {
        (status, subStatus) = ReadStatus(req);

        var input = string.Concat(
            req.PayoutId, req.SiteCode,
            req.MerchantReference, req.CustomerMerchantReference,
            status, subStatus, apiKey);

        return Sha512Lower(input) == req.HashCheck.ToLowerInvariant();
    }

    public static (int status, int subStatus) ReadStatus(PayoutNotificationRequest req)
    {
        if (req.PayoutStatus is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            return (
                el.GetProperty("status").GetInt32(),
                el.GetProperty("subStatus").GetInt32()
            );
        }

        var s = req.PayoutStatus is JsonElement flat && flat.ValueKind == JsonValueKind.Number
            ? flat.GetInt32()
            : Convert.ToInt32(req.PayoutStatus ?? 0);

        var ss = req.PayoutSubStatus ?? req.SubStatus ?? 0;
        return (s, ss);
    }
}
