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
        var siteCode   = body.TryGetProperty("siteCode", out var sc) ? sc.GetString() : null;
        var txRef      = body.TryGetProperty("merchantReference", out var tr) ? tr.GetString() : null;
        var smartRef   = body.TryGetProperty("smartReference", out var sr) ? sr.GetString() ?? "" : "";
        var status     = body.TryGetProperty("status", out var s) ? s.GetString() : null;
        var hashCheck  = body.TryGetProperty("hashCheck", out var h) ? h.GetString() : null;

        if (string.IsNullOrEmpty(txRef) || string.IsNullOrEmpty(status) || string.IsNullOrEmpty(hashCheck))
            return Ok();

        var clientSecret     = config["Ozow:OneApiClientSecret"]!;
        var resolvedSiteCode = siteCode ?? config["Ozow:SiteCode"]!;

        if (!hash.VerifyPaymentNotificationHash(resolvedSiteCode, txRef, smartRef, status, clientSecret, hashCheck))
        {
            logger.LogWarning("Payment notification hash invalid. Ref={Ref}", txRef);
            return Ok();
        }

        logger.LogInformation("Ozow payment notification: Ref={Ref} Status={Status}", txRef, status);

        if (status.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            var advanced = await txService.HandlePaymentReceivedAsync(txRef);
            if (!advanced)
                logger.LogWarning("Payment notification: no PaymentPending transaction found for {Ref}", txRef);
        }

        return Ok();
    }
}
