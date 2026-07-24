using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Webhooks;

[ApiController]
public class OzowNotificationController(HashService hash, AppDbContext db,
    TransactionService txService, IConfiguration config, ILogger<OzowNotificationController> logger) : ControllerBase
{
    [HttpPost("/securex/payout-notification")]
    public async Task<IActionResult> Notify([FromBody] PayoutNotificationRequest req)
    {
        var apiKey = config["Ozow:PayoutApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new ErrorResponse { Error = "Server misconfigured: missing OZOW_PAYOUT_API_KEY" });

        var missing = ValidateRequired(req);
        if (missing is not null)
            return Ok(new PayoutNotificationResponse { Received = true, HashValid = false, Reason = missing });

        var hashValid = hash.VerifyNotificationHash(req, apiKey, out var status, out var subStatus);
        if (!hashValid)
            return Ok(new PayoutNotificationResponse { Received = true, HashValid = false, Reason = "Invalid hash check" });

        var eventKey = $"{req.PayoutId}:{status}:{subStatus}";

        // Idempotency — attempt insert; skip if duplicate
        var duplicate = false;
        try
        {
            db.WebhookEvents.Add(new WebhookEvent
            {
                EventKey = eventKey,
                PayoutId = req.PayoutId,
                Status = status,
                SubStatus = subStatus,
            });
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            duplicate = true;
            db.ChangeTracker.Clear();
        }

        // Log every notification regardless
        db.PayoutNotifications.Add(new PayoutNotification
        {
            EventKey = eventKey,
            PayoutId = req.PayoutId,
            SiteCode = req.SiteCode,
            MerchantReference = req.MerchantReference,
            CustomerMerchantReference = req.CustomerMerchantReference,
            Status = status,
            SubStatus = subStatus,
            HashValid = true,
            Duplicate = duplicate,
            RawPayload = JsonSerializer.Serialize(req),
        });
        await db.SaveChangesAsync();

        // Handle terminal statuses
        if (!duplicate)
        {
            if (status == 5 && !string.IsNullOrEmpty(req.MerchantReference))
                await txService.HandlePayoutCompleteAsync(req.MerchantReference, req.PayoutId);
            else if (status is 99 or 4 or 90)
                logger.LogWarning("PayoutNotification: terminal non-complete. PayoutId={PayoutId} status={Status} subStatus={SubStatus} Ref={Ref}",
                    req.PayoutId, status, subStatus, req.MerchantReference);
        }

        return Ok(new PayoutNotificationResponse
        {
            Received = true,
            Processed = !duplicate,
            Duplicate = duplicate,
            HashValid = true,
            PayoutId = req.PayoutId,
        });
    }

    private static string? ValidateRequired(PayoutNotificationRequest req)
    {
        if (string.IsNullOrEmpty(req.PayoutId)) return "Missing field: payoutId";
        if (string.IsNullOrEmpty(req.SiteCode)) return "Missing field: siteCode";
        if (string.IsNullOrEmpty(req.MerchantReference)) return "Missing field: merchantReference";
        if (string.IsNullOrEmpty(req.CustomerMerchantReference)) return "Missing field: customerMerchantReference";
        if (string.IsNullOrEmpty(req.HashCheck)) return "Missing field: hashCheck";
        if (req.PayoutStatus is null) return "Missing field: payoutStatus";
        return null;
    }
}
