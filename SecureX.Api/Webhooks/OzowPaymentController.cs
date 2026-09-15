using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Services;

namespace SecureX.Api.Webhooks;

[ApiController]
[IgnoreAntiforgeryToken]
public class OzowPaymentController(
    TransactionService txService,
    HashService hash,
    IConfiguration config,
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

        if (!Request.HasFormContentType)
        {
            using var reader = new StreamReader(Request.Body);
            var rawBody = await reader.ReadToEndAsync();

            logger.LogWarning(
                "[OzowPayment] unsupported notification content type {ContentType}. BodyLength={Length}",
                Request.ContentType ?? "(null)",
                rawBody.Length);

            return Ok();
        }

        var form = await Request.ReadFormAsync();

        logger.LogInformation(
            "[OzowPayment] form notification fields: {Fields}",
            string.Join(", ", form.Keys));

        var siteCode = form["SiteCode"].FirstOrDefault();
        var transactionId = form["TransactionId"].FirstOrDefault();
        var transactionRef = form["TransactionReference"].FirstOrDefault();
        var amount = form["Amount"].FirstOrDefault();
        var status = form["Status"].FirstOrDefault();

        var optional1 = form["Optional1"].FirstOrDefault();
        var optional2 = form["Optional2"].FirstOrDefault();
        var optional3 = form["Optional3"].FirstOrDefault();
        var optional4 = form["Optional4"].FirstOrDefault();
        var optional5 = form["Optional5"].FirstOrDefault();

        var currencyCode = form["CurrencyCode"].FirstOrDefault();
        var isTest = form["IsTest"].FirstOrDefault();
        var statusMessage = form["StatusMessage"].FirstOrDefault();
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

        var configuredSiteCode = config["Ozow:SiteCode"];
        var privateKey = config["Ozow:PrivateKey"];

        if (string.IsNullOrWhiteSpace(configuredSiteCode) ||
            string.IsNullOrWhiteSpace(privateKey))
        {
            logger.LogError(
                "[OzowPayment] missing Ozow:SiteCode or Ozow:PrivateKey configuration");

            return StatusCode(500);
        }

        if (!string.Equals(
                siteCode?.Trim(),
                configuredSiteCode.Trim(),
                StringComparison.Ordinal))
        {
            logger.LogWarning(
                "[OzowPayment] rejected notification due to SiteCode mismatch");

            return Ok();
        }

        if (string.IsNullOrWhiteSpace(hashCheck))
        {
            logger.LogWarning(
                "[OzowPayment] rejected notification because Hash is missing. Ref={Ref}",
                transactionRef);

            return Ok();
        }

        if (string.IsNullOrWhiteSpace(transactionId) ||
            string.IsNullOrWhiteSpace(amount) ||
            string.IsNullOrWhiteSpace(currencyCode) ||
            string.IsNullOrWhiteSpace(isTest) ||
            string.IsNullOrWhiteSpace(statusMessage))
        {
            logger.LogWarning(
                "[OzowPayment] rejected notification because required hash fields are missing. Ref={Ref}",
                transactionRef);

            return Ok();
        }

        var hashValid = hash.VerifyPaymentNotificationHash(
            siteCode!,
            transactionId!,
            transactionRef!,
            amount!,
            status!,
            optional1,
            optional2,
            optional3,
            optional4,
            optional5,
            currencyCode!,
            isTest!,
            statusMessage!,
            privateKey,
            hashCheck);

        if (!hashValid)
        {
            logger.LogWarning(
                "[OzowPayment] payment notification hash verification failed. Ref={Ref} Status={Status} HashLength={HashLength}",
                transactionRef,
                status,
                hashCheck.Length);

            return Ok();
        }

        logger.LogInformation(
            "[OzowPayment] payment notification hash verified. Ref={Ref} Status={Status}",
            transactionRef,
            status);

        if (!status.Equals(
                "Complete",
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "[OzowPayment] non-complete notification acknowledged. Ref={Ref} Status={Status}",
                transactionRef,
                status);

            return Ok();
        }

        var advanced = await txService.HandlePaymentReceivedAsync(
            transactionRef);

        if (!advanced)
        {
            logger.LogInformation(
                "[OzowPayment] no PaymentPending transaction found for {Ref}. It may already have been processed.",
                transactionRef);

            return Ok();
        }

        logger.LogInformation(
            "[OzowPayment] payment secured successfully. Ref={Ref}",
            transactionRef);

        // Send transactional emails after the state transition has committed.
        // A fresh scope is used so the email work has independent scoped
        // services/DbContext instances.
        try
        {
            using var scope = scopeFactory.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

            var emailSvc = scope.ServiceProvider
                .GetRequiredService<EmailService>();

            var dealTokens = scope.ServiceProvider
                .GetRequiredService<DealTokenService>();

            var tx = await db.Transactions
                .Include(t => t.Buyer)
                .Include(t => t.Seller)
                .FirstOrDefaultAsync(
                    t => t.DealReference == transactionRef);

            if (tx is null)
            {
                logger.LogWarning(
                    "[OzowPayment] transaction not found after state transition. Ref={Ref}",
                    transactionRef);

                return Ok();
            }

            if (tx.Seller is not null)
            {
                var sellerToken = dealTokens.GenerateSellerToken(
                    tx.DealReference,
                    tx.Id);

                await emailSvc.SendSellerVerificationLinkAsync(
                    tx.Seller,
                    tx,
                    sellerToken);

                logger.LogInformation(
                    "[OzowPayment] seller verification email dispatched. Ref={Ref}",
                    transactionRef);
            }
            else
            {
                logger.LogWarning(
                    "[OzowPayment] transaction has no seller. Ref={Ref}",
                    transactionRef);
            }

            if (tx.Buyer is not null)
            {
                var buyerToken = dealTokens.GenerateBuyerToken(
                    tx.DealReference,
                    tx.Id);

                await emailSvc.SendEscrowFundedAsync(
                    tx.Buyer,
                    tx,
                    buyerToken);

                logger.LogInformation(
                    "[OzowPayment] buyer escrow-funded email dispatched. Ref={Ref}",
                    transactionRef);
            }
            else
            {
                logger.LogWarning(
                    "[OzowPayment] transaction has no buyer. Ref={Ref}",
                    transactionRef);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[OzowPayment] post-payment email dispatch failed. Ref={Ref}",
                transactionRef);
        }

        return Ok();
    }
}
