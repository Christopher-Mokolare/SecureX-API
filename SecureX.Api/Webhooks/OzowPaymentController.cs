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
    IServiceScopeFactory scopeFactory,
    ILogger<OzowPaymentController> logger) : ControllerBase
{
    [HttpPost("/securex/payment-notification")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> PaymentNotification()
    {
        logger.LogInformation(
            "[OzowPayment] notification received. ContentType={ContentType} ContentLength={ContentLength} UserAgent={UserAgent}",
            Request.ContentType ?? "(null)",
            Request.ContentLength?.ToString() ?? "(null)",
            Request.Headers.UserAgent.ToString());

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();

            logger.LogInformation(
                "[OzowPayment] form notification fields: {Fields}",
                string.Join(", ", form.Keys));

            var siteCode = form["SiteCode"].FirstOrDefault();
            var transactionId = form["TransactionId"].FirstOrDefault();
            var transactionRef = form["TransactionReference"].FirstOrDefault();
            var amount = form["Amount"].FirstOrDefault();
            var status = form["Status"].FirstOrDefault();
            var hashCheck = form["Hash"].FirstOrDefault();

            logger.LogInformation(
                "[OzowPayment] parsed notification: Ref={Ref} Status={Status} TransactionId={TransactionId} Amount={Amount}",
                transactionRef ?? "(null)",
                status ?? "(null)",
                transactionId ?? "(null)",
                amount ?? "(null)");

            if (string.IsNullOrWhiteSpace(transactionRef) ||
                string.IsNullOrWhiteSpace(status))
            {
                logger.LogWarning(
                    "[OzowPayment] missing TransactionReference or Status");

                return Ok();
            }

            // TEMPORARY PATCH:
            // Do not advance the transaction until the exact current
            // Payments API notification hash specification is confirmed.
            logger.LogWarning(
                "[OzowPayment] notification received successfully, but payment state was NOT advanced yet. HashPresent={HashPresent}",
                !string.IsNullOrWhiteSpace(hashCheck));

            return Ok();
        }

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        logger.LogWarning(
            "[OzowPayment] unsupported notification content type {ContentType}. BodyLength={Length}",
            Request.ContentType ?? "(null)",
            rawBody.Length);

        return Ok();
    }
}
