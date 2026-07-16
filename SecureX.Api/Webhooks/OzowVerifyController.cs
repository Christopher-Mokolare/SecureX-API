using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Webhooks;

[ApiController]
public class OzowVerifyController(HashService hash, IConfiguration config) : ControllerBase
{
    [HttpPost("/securex/payout-verify")]
    public IActionResult Verify([FromBody] PayoutVerifyRequest req)
    {
        var apiKey = config["Ozow:ApiKey"];
        var decryptionKey = config["Ozow:AccountNumberDecryptionKey"];

        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_API_KEY" });

        if (string.IsNullOrEmpty(decryptionKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY" });

        var missing = ValidateRequired(req);
        if (missing is not null)
            return Ok(Reject(req.PayoutId, missing));

        if (!hash.VerifyPayoutHash(req, apiKey))
            return Ok(Reject(req.PayoutId, "Invalid hash check"));

        return Ok(new PayoutVerifyResponse
        {
            PayoutId = req.PayoutId,
            IsVerified = true,
            AccountNumberDecryptionKey = decryptionKey,
        });
    }

    private static PayoutVerifyResponse Reject(string payoutId, string reason) => new()
    {
        PayoutId = payoutId,
        IsVerified = false,
        Reason = reason[..Math.Min(reason.Length, 50)],
    };

    private static string? ValidateRequired(PayoutVerifyRequest req)
    {
        if (string.IsNullOrEmpty(req.PayoutId)) return "Missing field: payoutId";
        if (string.IsNullOrEmpty(req.SiteCode)) return "Missing field: siteCode";
        if (string.IsNullOrEmpty(req.MerchantReference)) return "Missing field: merchantReference";
        if (string.IsNullOrEmpty(req.CustomerBankReference)) return "Missing field: customerBankReference";
        if (string.IsNullOrEmpty(req.NotifyUrl)) return "Missing field: notifyUrl";
        if (req.BankingDetails is null) return "Missing field: bankingDetails";
        if (string.IsNullOrEmpty(req.BankingDetails.BankGroupId)) return "Missing field: bankingDetails.bankGroupId";
        if (string.IsNullOrEmpty(req.BankingDetails.AccountNumber)) return "Missing field: bankingDetails.accountNumber";
        if (string.IsNullOrEmpty(req.BankingDetails.BranchCode)) return "Missing field: bankingDetails.branchCode";
        if (string.IsNullOrEmpty(req.HashCheck)) return "Missing field: hashCheck";
        return null;
    }
}
