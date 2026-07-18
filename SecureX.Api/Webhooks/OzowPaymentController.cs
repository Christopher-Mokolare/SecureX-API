using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Services;
using System.Text.Json;

namespace SecureX.Api.Webhooks;

[ApiController]
public class OzowPaymentController(
    TransactionService txService,
    HashService hash,
    IConfiguration config,
    ILogger<OzowPaymentController> logger) : ControllerBase
{
    [HttpPost("/securex/payment-notification")]
    public async Task<IActionResult> PaymentNotification([FromBody] JsonElement body)
    {
        var status            = body.TryGetProperty("status", out var s) ? s.GetString() : null;
        var merchantReference = body.TryGetProperty("merchantReference", out var r) ? r.GetString() : null;
        var hashCheck         = body.TryGetProperty("hashCheck", out var h) ? h.GetString() : null;

        if (string.IsNullOrEmpty(merchantReference) || string.IsNullOrEmpty(status) || string.IsNullOrEmpty(hashCheck))
            return Ok();

        var siteCode     = config["Ozow:SiteCode"]!;
        var clientSecret = config["Ozow:OneApiClientSecret"]!;

        if (!hash.VerifyPaymentNotificationHash(siteCode, merchantReference, status, clientSecret, hashCheck))
        {
            logger.LogWarning("Payment notification hash invalid. Ref={Ref}", merchantReference);
            return Ok();
        }

        logger.LogInformation("Ozow payment notification: Ref={Ref} Status={Status}", merchantReference, status);

        if (status.ToLowerInvariant() == "completed")
        {
            var advanced = await txService.HandlePaymentReceivedAsync(merchantReference);
            if (!advanced)
                logger.LogWarning("Payment notification: no PaymentPending transaction found for {Ref}", merchantReference);
        }

        return Ok();
    }
}
