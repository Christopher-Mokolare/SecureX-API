using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Models;
using SecureX.Api.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureX.Api.Webhooks;

[ApiController]
[IgnoreAntiforgeryToken]
public class OzowVerifyController(HashService hash, IConfiguration config, ILogger<OzowVerifyController> logger) : ControllerBase
{
    [HttpPost("/securex/payout-verify")]
    public async Task<IActionResult> Verify()
    {
        Request.EnableBuffering();
        var rawBody = await new System.IO.StreamReader(Request.Body).ReadToEndAsync();
        Request.Body.Position = 0;
        logger.LogInformation("PayoutVerify RAW headers: AccessToken={Token}",
            Request.Headers["AccessToken"].FirstOrDefault()?.Replace("\n","").Replace("\r",""));
        logger.LogInformation("PayoutVerify RAW body: {Body}", rawBody);

        PayoutVerifyRequest req;
        try { req = System.Text.Json.JsonSerializer.Deserialize<PayoutVerifyRequest>(rawBody, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return BadRequest(); }

        var apiKey = config["Ozow:PayoutApiKey"];
        var decryptionKey = config["Ozow:AccountNumberDecryptionKey"];

        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_PAYOUT_API_KEY" });

        if (string.IsNullOrEmpty(decryptionKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_ACCOUNT_NUMBER_DECRYPTION_KEY" });

        var missing = ValidateRequired(req);
        if (missing is not null)
            return Ok(Reject(req.PayoutId, missing));

        var expectedAccessToken = config["Ozow:AccessToken"]?.Trim();
        var receivedAccessToken = Request.Headers["AccessToken"].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(expectedAccessToken) ||
            string.IsNullOrEmpty(receivedAccessToken) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(receivedAccessToken),
                Encoding.UTF8.GetBytes(expectedAccessToken)))
            return Ok(Reject(req.PayoutId ?? "", "Invalid access token"));

        if (!hash.VerifyPayoutHash(req, apiKey))
        {
            var cents = (long)Math.Round(req.Amount * 100);
            var debugInput = string.Concat(
                req.PayoutId, req.SiteCode, cents,
                req.MerchantReference, req.CustomerBankReference,
                req.IsRtc.ToString().ToLowerInvariant(), req.NotifyUrl,
                req.BankingDetails?.BankGroupId, req.BankingDetails?.AccountNumber,
                req.BankingDetails?.BranchCode, apiKey);
            logger.LogWarning("PayoutVerify: hash mismatch payoutId={PayoutId} received={Received} inputLower={Input}",
                req.PayoutId?.Replace("\n","").Replace("\r",""),
                req.HashCheck,
                debugInput.ToLowerInvariant());
            return Ok(Reject(req.PayoutId ?? "", "Invalid hash check"));
        }

        logger.LogInformation("PayoutVerify: verified payoutId={PayoutId}",
            req.PayoutId?.Replace("\n", "").Replace("\r", ""));
        return Ok(new PayoutVerifyResponse
        {
            PayoutId = req.PayoutId ?? "",
            IsVerified = true,
            AccountNumberDecryptionKey = decryptionKey!,
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
        if (string.IsNullOrEmpty(req.VerifyUrl)) return "Missing field: verifyUrl";
        return null;
    }
}
