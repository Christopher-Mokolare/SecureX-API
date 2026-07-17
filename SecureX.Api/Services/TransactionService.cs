using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class TransactionService(AppDbContext db, DealReferenceService refService, OzowPayoutService payoutService, ThisIsMeAvsService avsService, IConfiguration config, ILogger<TransactionService> logger)
{
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
            Status = TransactionStatus.Initialized,
        };

        db.Transactions.Add(tx);
        AppendAudit(tx, null, TransactionStatus.Initialized, "buyer", "Transaction created via signup form");
        await db.SaveChangesAsync();
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
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET version = version + 1 WHERE \"Id\" = {0}", txId);
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
            merchantReference, payoutId, tx.Status);
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

    // ── Step 4/7: KYC result ─────────────────────────────────────────────────

    public async Task HandleKycResultAsync(Guid userId, bool approved)
    {
        var buyerTx = await db.Transactions.FirstOrDefaultAsync(
            t => t.BuyerId == userId && t.Status == TransactionStatus.BuyerKycPending);
        if (buyerTx is not null)
        {
            var next = approved ? TransactionStatus.PaymentPending : TransactionStatus.BuyerKycFailed;
            await AdvanceStateAsync(buyerTx.Id, TransactionStatus.BuyerKycPending,
                next, "smileid-webhook", $"Buyer KYC {(approved ? "approved" : "failed")}", buyerTx.Version);
            return;
        }

        var sellerTx = await db.Transactions.FirstOrDefaultAsync(
            t => t.SellerId == userId && t.Status == TransactionStatus.SellerKycPending);
        if (sellerTx is not null)
        {
            var next = approved ? TransactionStatus.LogisticsPending : TransactionStatus.RequiresRefund;
            await AdvanceStateAsync(sellerTx.Id, TransactionStatus.SellerKycPending,
                next, "smileid-webhook", $"Seller KYC {(approved ? "approved" : "failed")}", sellerTx.Version);
        }
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
        var seller = tx.Seller ?? await db.Users.FindAsync(tx.SellerId);
        if (seller is null)
        {
            logger.LogError("TriggerPayout: seller {SellerId} not found for {Ref}", tx.SellerId, tx.DealReference);
            return;
        }

        if (string.IsNullOrWhiteSpace(seller.BankAccountNumber) ||
            string.IsNullOrWhiteSpace(seller.BankBranchCode))
        {
            logger.LogWarning("TriggerPayout: seller {SellerId} has no bank details — payout skipped", tx.SellerId);
            return;
        }

        var notifyUrl  = config["Ozow:NotifyUrl"]!;
        var encKey     = config["Ozow:AccountNumberDecryptionKey"]!;
        var payoutAmount = tx.ItemValue - tx.SellerFee;

        // BankGroupId stored on user — populated when seller adds bank details
        var bankGroupId = seller.BankGroupId;

        // Verify seller bank account via ThisIsMe AVS before releasing funds
        var bankVerified = await avsService.VerifyBankAccountAsync(
            seller.IdNumber, seller.BankAccountNumber, seller.BankBranchCode);

        if (!bankVerified)
        {
            logger.LogWarning("TriggerPayout: AVS failed for seller {SellerId} — payout blocked", tx.SellerId);
            seller.BankVerificationStatus = KycStatus.Failed;
            await db.SaveChangesAsync();
            return;
        }

        seller.BankVerificationStatus = KycStatus.Approved;
        await db.SaveChangesAsync();

        var payoutId = await payoutService.RequestPayoutAsync(
            tx.DealReference, payoutAmount,
            bankGroupId, seller.BankAccountNumber,
            seller.BankBranchCode, encKey, notifyUrl);

        if (payoutId is null)
            logger.LogError("TriggerPayout: Ozow rejected payout for {Ref}", tx.DealReference);
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
