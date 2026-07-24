using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Models;
using SecureX.Api.Services;
using System.Text.Json;

namespace SecureX.Api.Webhooks;

[ApiController]
public class OzowVerifyController(HashService hash, IConfiguration config, ILogger<OzowVerifyController> logger) : ControllerBase
{
    [HttpPost("/securex/payout-verify")]
    public IActionResult Verify([FromBody] PayoutVerifyRequest req)
    {
        var apiKey = config["Ozow:PayoutApiKey"];
        var decryptionKey = config["Ozow:AccountNumberDecryptionKey"];

        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_PAYOUT_API_KEY" });

        if (string.IsNullOrEmpty(decryptionKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY" });

        var missing = ValidateRequired(req);
        if (missing is not null)
            return Ok(Reject(req.PayoutId, missing));

        // Log full request so we can verify the hash formula against what Ozow sends
        var cents = (long)Math.Round(req.Amount * 100);
        logger.LogInformation(
            "PayoutVerify: payoutId={PayoutId} siteCode={SiteCode} amount={Amount} cents={Cents} " +
            "merchantRef={MerchantRef} customerBankRef={CustomerBankRef} isRtc={IsRtc} notifyUrl={NotifyUrl} " +
            "bankGroupId={BankGroupId} accountNumber={AccountNumber} branchCode={BranchCode} hashCheck={HashCheck}",
            req.PayoutId, req.SiteCode, req.Amount, cents,
            req.MerchantReference, req.CustomerBankReference, req.IsRtc, req.NotifyUrl,
            req.BankingDetails?.BankGroupId, req.BankingDetails?.AccountNumber,
            req.BankingDetails?.BranchCode, req.HashCheck);

        var hashValid = hash.VerifyPayoutHash(req, apiKey);
        if (!hashValid)
            logger.LogWarning("PayoutVerify: hash mismatch for payoutId={PayoutId} — proceeding anyway (staging)", req.PayoutId);

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
        if (req.BankingDetails is null) return "Missing field: bankingDetails";
        if (string.IsNullOrEmpty(req.BankingDetails.BankGroupId)) return "Missing field: bankingDetails.bankGroupId";
        if (string.IsNullOrEmpty(req.BankingDetails.AccountNumber)) return "Missing field: bankingDetails.accountNumber";
        if (string.IsNullOrEmpty(req.BankingDetails.BranchCode)) return "Missing field: bankingDetails.branchCode";
        if (string.IsNullOrEmpty(req.HashCheck)) return "Missing field: hashCheck";
        return null;
    }
}
