using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class TransactionService(AppDbContext db, DealReferenceService refService, OzowPayoutService payoutService, SmileIdService smileId, IConfiguration config, ILogger<TransactionService> logger, IServiceScopeFactory scopeFactory)
{
    public async Task<string?> CreateSellerLivenessTokenAsync(Transaction tx)
    {
        if (tx.Seller is null)
            return null;

        if (tx.Seller.AmlStatus == KycStatus.Pending &&
            string.IsNullOrWhiteSpace(tx.Seller.SmileIdAmlJobId))
        {
            var aml = await smileId.SubmitAmlAsync(tx.Seller.FullName, tx.DealReference, userId: tx.Seller.Id.ToString());
            if (aml is not null)
            {
                tx.Seller.SmileIdAmlJobId = aml.JobId;
                tx.Seller.AmlStatus = aml.ResultCode switch
                {
                    "1031" => KycStatus.Approved,
                    "1030" => KycStatus.Failed,
                    _ => KycStatus.Pending
                };
                tx.Seller.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        return await smileId.CreateBiometricKycTokenAsync();
    }

    // ── Fee calculation (matches frontend js/script.js) ──────────────────────
    // Standard:         max(value * 2.5%, R150)
    // VerifiedExpress:  max(value * 1.5%, R150) + R250

    public static decimal CalculateFee(decimal itemValue, ServiceType type) => type switch
    {
        ServiceType.Standard => Math.Max(itemValue * 0.025m, 150m),
        ServiceType.VerifiedExpress => Math.Max(itemValue * 0.015m, 150m) + 250m,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static (decimal buyerFee, decimal sellerFee) SplitFee(decimal fee, FeePayer payer) => payer switch
    {
        FeePayer.Buyer => (fee, 0m),
        FeePayer.Seller => (0m, fee),
        FeePayer.Split => (Math.Round(fee / 2, 2), Math.Round(fee / 2, 2)),
        _ => (fee, 0m)
    };

    // ── Step 3: Create transaction ───────────────────────────────────────────

    public async Task<Transaction> CreateAsync(CreateTransactionRequest req)
    {
        // ── Buyer KYC: submit Enhanced KYC job (async — result arrives via webhook) ──
        if (string.IsNullOrWhiteSpace(req.BuyerIdNumber))
            throw new InvalidOperationException("Buyer ID number is required for KYC verification");

        // Upsert buyer
        var buyer = await db.Users.FirstOrDefaultAsync(u => u.Email == req.BuyerEmail);
        if (buyer is null)
        {
            buyer = new User { FullName = req.BuyerFullName, Email = req.BuyerEmail, Phone = req.BuyerPhone };
            db.Users.Add(buyer);
        }

        // Upsert seller
        var seller = await db.Users.FirstOrDefaultAsync(u => u.Email == req.SellerEmail);
        if (seller is null)
        {
            seller = new User { FullName = req.SellerFullName, Email = req.SellerEmail, Phone = req.SellerPhone };
            db.Users.Add(seller);
        }

        await db.SaveChangesAsync(); // commit users first so FK is satisfied

        var fee = CalculateFee(req.ItemValue, req.ServiceType);
        var (buyerFee, sellerFee) = SplitFee(fee, req.FeePayer);

        var tx = new Transaction
        {
            DealReference = await refService.NextAsync(),
            BuyerId = buyer.Id,
            SellerId = seller.Id,
            ItemTitle = req.ItemTitle,
            ItemDescription = req.ItemDescription,
            SellerLocation = req.SellerLocation,
            ItemValue = req.ItemValue,
            PlatformFee = fee,
            BuyerFee = buyerFee,
            SellerFee = sellerFee,
            TotalCheckoutAmount = req.ItemValue + buyerFee,
            ServiceType = req.ServiceType,
            Status = TransactionStatus.PaymentPending,
        };

        db.Transactions.Add(tx);
        AppendAudit(tx, null, TransactionStatus.PaymentPending, "system", "Transaction created — awaiting buyer KYC");
        await db.SaveChangesAsync();

        // Submit KYC job — result arrives asynchronously via SmileID webhook
        var jobId = await smileId.SubmitEnhancedKycAsync(
            req.BuyerFullName, req.BuyerIdNumber, req.BuyerEmail, req.BuyerPhone, tx.DealReference);

        if (jobId is not null)
        {
            buyer.SmileIdJobId = jobId;
            buyer.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation("SmileID KYC job submitted: {JobId} for deal {Ref}", jobId, tx.DealReference);
        }
        else
        {
            logger.LogWarning("SmileID KYC job submission failed for deal {Ref} — KYC status remains Pending", tx.DealReference);
        }

        // AML is a separate watchlist screening and must not be inferred from ID verification.
        var aml = await smileId.SubmitAmlAsync(req.BuyerFullName, tx.DealReference, userId: buyer.Id.ToString());
        if (aml is not null)
        {
            buyer.SmileIdAmlJobId = aml.JobId;
            buyer.AmlStatus = aml.ResultCode switch
            {
                "1031" => KycStatus.Approved, // Not found on list
                "1030" => KycStatus.Failed,   // Found on list
                _ => KycStatus.Pending
            };
            buyer.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        else
        {
            logger.LogWarning("SmileID AML submission failed for deal {Ref} — AML status remains Pending", tx.DealReference);
        }

        tx.Buyer = buyer;
        tx.Seller = seller;
        return tx;
    }

    // ── Generic state advance with row lock + optimistic concurrency ─────────

    public async Task<Transaction> AdvanceStateAsync(Guid txId, TransactionStatus expected,
        TransactionStatus next, string actor, string details, int expectedVersion)
    {
        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == txId)
            ?? throw new KeyNotFoundException($"Transaction {txId} not found");

        if (tx.Status != expected)
            throw new InvalidOperationException($"Expected status {expected}, got {tx.Status}");

        if (tx.Version != expectedVersion)
            throw new DbUpdateConcurrencyException($"Version mismatch: expected {expectedVersion}, got {tx.Version}");

        var prev = tx.Status;
        tx.Status = next;
        tx.UpdatedAt = DateTime.UtcNow;

        if (next == TransactionStatus.ItemDelivered)
            tx.InspectionWindowEndsAt = DateTime.UtcNow.AddHours(24);

        // Add directly to DbSet — never via navigation property to avoid EF tracking existing audit logs as Modified
        db.AuditLogs.Add(new AuditLog
        {
            TransactionId = txId,
            PreviousStatus = prev,
            NewStatus = next,
            TriggerActor = actor,
            ActionDetails = details,
        });

        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE transactions SET version = version + 1 WHERE \"Id\" = {txId}");
        tx.Version++;
        return tx;
    }

    // ── Ozow payout-notification: PayoutComplete (status=5) → log only ──────
    // The payout-notification webhook is for SELLER payout outcomes, not buyer payment.
    // Buyer payment received is handled separately via the collection webhook.

    public async Task<bool> HandlePayoutCompleteAsync(string merchantReference, string payoutId)
    {
        var tx = await db.Transactions.FirstOrDefaultAsync(
            t => t.DealReference == merchantReference);
        if (tx is null) return false;

        logger.LogInformation("Ozow payout complete. Ref={Ref} PayoutId={PayoutId} Status={Status}",
            merchantReference.Replace("\n", "").Replace("\r", ""),
            payoutId.Replace("\n", "").Replace("\r", ""),
            tx.Status);
        return true;
    }

    // ── Buyer payment received → FundsSecured ────────────────────────────────
    // Called by the Ozow collection payment webhook (separate from payout-notification)

    public async Task<bool> HandlePaymentReceivedAsync(string merchantReference)
    {
        var tx = await db.Transactions.FirstOrDefaultAsync(
            t => t.DealReference == merchantReference && t.Status == TransactionStatus.PaymentPending);
        if (tx is null) return false;

        await AdvanceStateAsync(tx.Id, TransactionStatus.PaymentPending,
            TransactionStatus.FundsSecured, "ozow-collection", "Buyer payment received", tx.Version);
        return true;
    }

    // ── Dispute resolution — admin decides outcome ───────────────────────────

    public async Task<Transaction> ResolveDisputeAsync(Guid txId, string decision, string actor)
    {
        var tx = await db.Transactions
            .Include(t => t.Seller)
            .Include(t => t.Buyer)
            .FirstOrDefaultAsync(t => t.Id == txId)
            ?? throw new KeyNotFoundException($"Transaction {txId} not found");

        if (tx.Status != TransactionStatus.RequiresRefund)
            throw new InvalidOperationException($"Transaction is not in RequiresRefund status (current: {tx.Status})");

        if (decision == "release-to-seller")
        {
            var updated = await AdvanceStateAsync(txId, TransactionStatus.RequiresRefund,
                TransactionStatus.Completed, actor, "Dispute resolved: released to seller", tx.Version);
            _ = TriggerPayoutAsync(updated);
            return updated;
        }

        if (decision == "refund-to-buyer")
        {
            // Refund is processed manually via Ozow dashboard — advance to Refunded terminal state
            return await AdvanceStateAsync(txId, TransactionStatus.RequiresRefund,
                TransactionStatus.Refunded, actor, "Dispute resolved: refund to buyer — process manually via Ozow dashboard", tx.Version);
        }

        throw new ArgumentException($"Invalid decision '{decision}'. Use 'release-to-seller' or 'refund-to-buyer'");
    }

    // ── Step 9: Buyer accepts → COMPLETED → trigger payout ──────────────────

    public async Task<Transaction> CompleteAsync(Guid txId, string actor, int expectedVersion)
    {
        var tx = await AdvanceStateAsync(txId, TransactionStatus.ItemDelivered,
            TransactionStatus.Completed, actor, "Buyer accepted item", expectedVersion);
        _ = TriggerPayoutAsync(tx);
        return tx;
    }

    // ── Dispute: buyer rejects within 24hr window ────────────────────────────

    public async Task<Transaction> RejectItemAsync(Guid txId, string reason, int expectedVersion)
    {
        var tx = await db.Transactions.FindAsync(txId)
            ?? throw new KeyNotFoundException($"Transaction {txId} not found");

        if (tx.Status != TransactionStatus.ItemDelivered)
            throw new InvalidOperationException("Item must be in ItemDelivered status to reject");

        if (tx.InspectionWindowEndsAt.HasValue && DateTime.UtcNow > tx.InspectionWindowEndsAt.Value)
            throw new InvalidOperationException("24-hour inspection window has expired");

        return await AdvanceStateAsync(txId, TransactionStatus.ItemDelivered,
            TransactionStatus.RequiresRefund, "buyer", $"Buyer rejected item: {reason}", expectedVersion);
    }

    private async Task TriggerPayoutAsync(Transaction tx)
    {
        try
        {
            logger.LogInformation("TriggerPayout: starting for {Ref} seller={SellerId}", tx.DealReference, tx.SellerId);

            // Fire-and-forget runs after the HTTP request scope is disposed.
            // Create a fresh scope so we get a live DbContext.
            await using var scope = scopeFactory.CreateAsyncScope();
            var freshDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var seller = await freshDb.Users.FindAsync(tx.SellerId);
            if (seller is null)
            {
                logger.LogError("TriggerPayout: seller {SellerId} not found for {Ref}", tx.SellerId, tx.DealReference);
                return;
            }

            if (seller.IdCheckStatus != KycStatus.Approved ||
                seller.AmlStatus != KycStatus.Approved ||
                seller.LivenessStatus != KycStatus.Approved)
            {
                logger.LogWarning("TriggerPayout: seller verification incomplete for {Ref}. KYC={Kyc} AML={Aml} Liveness={Liveness}",
                    tx.DealReference, seller.IdCheckStatus, seller.AmlStatus, seller.LivenessStatus);
                return;
            }

            if (string.IsNullOrWhiteSpace(seller.BankAccountNumber) ||
                string.IsNullOrWhiteSpace(seller.BankBranchCode))
            {
                logger.LogWarning("TriggerPayout: seller {SellerId} has no bank details — payout skipped. Account='{Account}' Branch='{Branch}'",
                    tx.SellerId, seller.BankAccountNumber, seller.BankBranchCode);
                return;
            }

            var notifyUrl    = config["Ozow:NotifyUrl"] ?? "";
            var verifyUrl    = config["Ozow:VerifyUrl"] ?? "";
            var encKey       = config["Ozow:AccountNumberDecryptionKey"] ?? "";
            var payoutAmount = tx.ItemValue - tx.SellerFee;

            logger.LogInformation("TriggerPayout: dispatching R{Amount} to bank={BankGroupId} notifyUrl={NotifyUrl}",
                payoutAmount, seller.BankGroupId, notifyUrl);

            var payoutId = await payoutService.RequestPayoutAsync(
                tx.DealReference, payoutAmount,
                seller.BankGroupId, seller.BankAccountNumber,
                seller.BankBranchCode, encKey, notifyUrl, verifyUrl);

            if (payoutId is null)
                logger.LogError("TriggerPayout: Ozow rejected payout for {Ref}", tx.DealReference);
            else
            {
                logger.LogInformation("TriggerPayout: success PayoutId={PayoutId} Ref={Ref}", payoutId, tx.DealReference);
                freshDb.PendingPayouts.Add(new PendingPayout
                {
                    PayoutId = payoutId,
                    DealReference = tx.DealReference,
                });
                await freshDb.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TriggerPayout: unhandled exception for {Ref}", tx.DealReference);
        }
    }

    private static void AppendAudit(Transaction tx, TransactionStatus? prev,
        TransactionStatus next, string actor, string details)
    {
        tx.AuditLogs.Add(new AuditLog
        {
            TransactionId = tx.Id,
            PreviousStatus = prev,
            NewStatus = next,
            TriggerActor = actor,
            ActionDetails = details,
        });
    }
}
