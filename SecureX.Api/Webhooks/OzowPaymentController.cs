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
    public async Task<IActionResult> PaymentNotification([FromBody] JsonElement body)
    {
        var siteCode  = body.TryGetProperty("SiteCode", out var sc) ? sc.GetString() : null;
        var txRef     = body.TryGetProperty("TransactionReference", out var tr) ? tr.GetString() : null;
        var smartRef  = body.TryGetProperty("SmartReference", out var sr) ? sr.GetString() ?? "" : "";
        var status    = body.TryGetProperty("Status", out var s) ? s.GetString() : null;
        var hashCheck = body.TryGetProperty("Hash", out var h) ? h.GetString() : null;

        logger.LogInformation("[OzowPayment] received notification: Ref={Ref} Status={Status}",
            txRef ?? "(null)", status ?? "(null)");

        if (string.IsNullOrEmpty(txRef) || string.IsNullOrEmpty(status))
        {
            logger.LogWarning("[OzowPayment] missing txRef or status; ignoring");
            return Ok();
        }

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
                logger.LogWarning(
                    "[OzowPayment] hash verification failed. Ref={Ref} Status={Status} hashLen={HashLen}",
                    txRef, status, hashCheck?.Length ?? 0);
                return Ok();
            }
        }

        logger.LogInformation("Ozow payment notification: Ref={Ref} Status={Status}", txRef, status);

        if (status.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            var advanced = await txService.HandlePaymentReceivedAsync(txRef);
            if (!advanced)
            {
                logger.LogWarning("Payment notification: no PaymentPending transaction found for {Ref}", txRef);
            }
            else
            {
                // Fire-and-forget: notify seller (with verification link) and buyer (confirmation).
                // Wrapped in Task.Run so we return 200 to Ozow immediately.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<SecureX.Api.Data.AppDbContext>();
                        var emailSvc = scope.ServiceProvider.GetRequiredService<SecureX.Api.Services.EmailService>();
                        var dealTokens = scope.ServiceProvider.GetRequiredService<SecureX.Api.Services.DealTokenService>();

                        var tx = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                            .FirstOrDefaultAsync(
                                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                                    .Include(
                                        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                                            .Include(db.Transactions, t => t.Buyer),
                                        t => t.Seller),
                                t => t.DealReference == txRef);

                        if (tx?.Seller is not null)
                        {
                            var sellerToken = dealTokens.GenerateSellerToken(tx.DealReference, tx.Id);
                            await emailSvc.SendSellerVerificationLinkAsync(tx.Seller, tx, sellerToken);
                        }
                        if (tx?.Buyer is not null)
                        {
                            var buyerToken = dealTokens.GenerateBuyerToken(tx.DealReference, tx.Id);
                            await emailSvc.SendEscrowFundedAsync(tx.Buyer, tx, buyerToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Post-payment email dispatch failed for {Ref}", txRef);
                    }
                });
            }
        }

        return Ok();
    }
}
