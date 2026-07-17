using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Webhooks;

/// <summary>
/// Handles Ozow payment collection notifications — called when a buyer completes payment.
/// This is separate from /securex/payout-notification which handles seller payouts.
/// </summary>
[ApiController]
public class OzowPaymentController(
    OzowCollectionService collectionService,
    TransactionService txService,
    ILogger<OzowPaymentController> logger) : ControllerBase
{
    [HttpPost("/securex/payment-notification")]
    public async Task<IActionResult> PaymentNotification([FromForm] OzowPaymentNotification req)
    {
        logger.LogInformation("Ozow payment notification: Ref={Ref} Status={Status} Amount={Amount}",
            req.TransactionReference, req.Status, req.Amount);

        // Verify hash
        if (!collectionService.VerifyPaymentNotificationHash(
            req.SiteCode, req.Amount, req.Status,
            req.TransactionReference, req.Optional1 ?? "", req.HashCheck))
        {
            logger.LogWarning("Ozow payment notification hash invalid for {Ref}", req.TransactionReference);
            return Ok(); // always 200 to Ozow
        }

        // Only advance state on successful payment
        if (req.Status?.ToLowerInvariant() == "complete")
        {
            var advanced = await txService.HandlePaymentReceivedAsync(req.TransactionReference);

            if (!advanced)
                logger.LogWarning("Payment notification: no PaymentPending transaction found for {Ref}",
                    req.TransactionReference);
        }

        return Ok();
    }
}
