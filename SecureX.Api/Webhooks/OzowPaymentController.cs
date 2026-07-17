using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Services;
using System.Text.Json;

namespace SecureX.Api.Webhooks;

[ApiController]
public class OzowPaymentController(
    TransactionService txService,
    ILogger<OzowPaymentController> logger) : ControllerBase
{
    [HttpPost("/securex/payment-notification")]
    public async Task<IActionResult> PaymentNotification([FromBody] JsonElement body)
    {
        var status = body.TryGetProperty("status", out var s) ? s.GetString() : null;
        var merchantReference = body.TryGetProperty("merchantReference", out var r) ? r.GetString() : null;

        logger.LogInformation("Ozow payment notification: Ref={Ref} Status={Status}", merchantReference, status);

        if (string.IsNullOrEmpty(merchantReference))
            return Ok();

        // One API sends "completed" on success
        if (status?.ToLowerInvariant() == "completed")
        {
            var advanced = await txService.HandlePaymentReceivedAsync(merchantReference);
            if (!advanced)
                logger.LogWarning("Payment notification: no PaymentPending transaction found for {Ref}", merchantReference);
        }

        return Ok();
    }
}
