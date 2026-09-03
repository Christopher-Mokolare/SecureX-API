using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Services;
using System.Text.Json;

namespace SecureX.Api.Webhooks;

[ApiController]
[IgnoreAntiforgeryToken]
public class OzowPaymentController(
    TransactionService txService,
    HashService hash,
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<OzowPaymentController> logger) : ControllerBase
{
    [HttpPost("/securex/payment-notification")]
    public async Task<IActionResult> PaymentNotification([FromBody] JsonElement body)
    {
        var siteCode  = body.TryGetProperty("SiteCode", out var sc) ? sc.GetString() : null;
        var txRef     = body.TryGetProperty("TransactionReference", out var tr) ? tr.GetString() : null;
        var smartRef  = body.TryGetProperty("SmartReference", out var sr) ? sr.GetString() ?? "" : "";
        var status    = body.TryGetProperty("Status", out var s) ? s.GetString() : null;
        var hashCheck = body.TryGetProperty("Hash", out var h) ? h.GetString() : null;

        if (string.IsNullOrEmpty(txRef) || string.IsNullOrEmpty(status))
            return Ok();

        var privateKey       = config["Ozow:PrivateKey"]!;
        var resolvedSiteCode = siteCode ?? config["Ozow:SiteCode"]!;
        var accessToken      = config["Ozow:AccessToken"];

        var isAdminOverride = (env.IsDevelopment() || env.IsStaging())
            && Request.Headers.TryGetValue("Authorization", out var auth)
            && auth.ToString() == $"Bearer {accessToken}";

        if (!isAdminOverride)
        {
            if (string.IsNullOrEmpty(hashCheck)) return Ok();
            if (!hash.VerifyPaymentNotificationHash(resolvedSiteCode, txRef, smartRef, status, privateKey, hashCheck))
            {
                logger.LogWarning("Payment notification hash invalid. Ref={Ref}", txRef);
                return Ok();
            }
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
