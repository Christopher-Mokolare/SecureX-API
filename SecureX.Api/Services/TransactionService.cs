using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class TransactionService(
    AppDbContext db,
    DealReferenceService refService,
    OzowPayoutService payoutService,
    SmileIdService smileId,
    IConfiguration config,
    ILogger<TransactionService> logger,
    IServiceScopeFactory scopeFactory)
{
    // ── Fee structure constants ──────────────────────────────────────────────
    // Transaction < R5,000  → R150
    // R5,000 ≤ Transaction < R8,000 → R200  
    // Transaction ≥ R8,000 → 2.5% of transaction value
    
    private const decimal Tier1Threshold = 5000m;
    private const decimal Tier2Threshold = 8000m;
    private const decimal Tier1Fee = 150m;
    private const decimal Tier2Fee = 200m;
    private const decimal PercentageRate = 0.025m;

    public static decimal CalculateStandardFee(decimal itemValue)
    {
        if (itemValue <= 0) return 0m;
        
        if (itemValue < Tier1Threshold)
            return Tier1Fee;
        else if (itemValue >= Tier1Threshold && itemValue < Tier2Threshold)
            return Tier2Fee;
        else
            return itemValue * PercentageRate;
    }

    public static decimal CalculateVerifiedExpressFee(decimal itemValue)
    {
        // Verified Express: 1.5% + R250, but never less than the standard fee
        var standardFee = CalculateStandardFee(itemValue);
        var expressFee = (itemValue * 0.015m) + 250m;
        return Math.Max(standardFee, expressFee);
    }

    public static decimal CalculateFee(decimal itemValue, ServiceType type) => type switch
    {
        ServiceType.Standard => CalculateStandardFee(itemValue),
        ServiceType.VerifiedExpress => CalculateVerifiedExpressFee(itemValue),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static (decimal buyerFee, decimal sellerFee) SplitFee(decimal fee, FeePayer payer) => payer switch
    {
        FeePayer.Buyer => (fee, 0m),
        FeePayer.Seller => (0m, fee),
        FeePayer.Split => (Math.Round(fee / 2, 2), Math.Round(fee / 2, 2)),
        _ => (fee, 0m)
    };

    public async Task<string?> CreateSellerVerificationTokenAsync(Transaction tx)
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

        // ─────────────────────────────────────────────────────────────────
        // TESTING MODE: Fees are forced to R0 in production while we finalize
        // the fee structure. To re-enable real fees, remove the env var
        //   Features__FreeFees=true
        // or set it to "false".
        // ─────────────────────────────────────────────────────────────────
        var isTestingMode = string.Equals(
            config["Features:FreeFees"], "true", StringComparison.OrdinalIgnoreCase);
        var fee = isTestingMode ? 0m : CalculateFee(req.ItemValue, req.ServiceType);
        var (buyerFee, sellerFee) = isTestingMode
            ? (0m, 0m)
            : SplitFee(fee, req.FeePayer);

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

        // NOTE: KYC and AML submissions are now triggered asynchronously
        // by the controller AFTER this method returns. See SubmitKycAndAmlAsync.

        return tx;
    }

    /// <summary>
    /// Runs SmileID KYC and AML submissions in the background.
    /// Called from the controller after returning the transaction to the client.
    /// </summary>
    public async Task SubmitKycAndAmlAsync(Guid transactionId, string buyerIdNumber)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scopedSmileId = scope.ServiceProvider.GetRequiredService<SmileIdService>();
        var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<TransactionService>>();

        try
        {
            var tx = await scopedDb.Transactions
                .Include(t => t.Buyer)
                .FirstOrDefaultAsync(t => t.Id == transactionId);

            if (tx?.Buyer is null)
            {
                scopedLogger.LogWarning("SubmitKycAndAmlAsync: transaction or buyer not found for {Id}", transactionId);
                return;
            }

            var buyer = tx.Buyer;

            // Submit KYC
            var smileBaseUrl = config["SmileId:BaseUrl"] ?? "";
            var isSandbox = smileBaseUrl.Contains("testapi", StringComparison.OrdinalIgnoreCase) ||
                            smileBaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
            var kycIdNumber = isSandbox ? "0000000000000" : buyerIdNumber;

            var jobId = await scopedSmileId.SubmitEnhancedKycAsync(
                buyer.FullName, kycIdNumber, buyer.Email, buyer.Phone, tx.DealReference);

            if (jobId is not null)
            {
                buyer.SmileIdJobId = jobId;
                buyer.UpdatedAt = DateTime.UtcNow;
                await scopedDb.SaveChangesAsync();
                scopedLogger.LogInformation("SmileID KYC job submitted: {JobId} for deal {Ref}", jobId, tx.DealReference);
            }
            else
            {
                scopedLogger.LogWarning("SmileID KYC job submission failed for deal {Ref}", tx.DealReference);
            }

            // Small delay to avoid sandbox rate-limiting
            await Task.Delay(3000);

            // Submit AML
            var aml = await scopedSmileId.SubmitAmlAsync(buyer.FullName, tx.DealReference, userId: buyer.Id.ToString());
            if (aml is null)
            {
                await Task.Delay(5000);
                aml = await scopedSmileId.SubmitAmlAsync(buyer.FullName, tx.DealReference, userId: buyer.Id.ToString());
            }

            if (aml is not null)
            {
                buyer.SmileIdAmlJobId = aml.JobId;
                buyer.AmlStatus = aml.ResultCode switch
                {
                    "1031" => KycStatus.Approved,
                    "1030" => KycStatus.Failed,
                    _ => KycStatus.Pending
                };
                buyer.UpdatedAt = DateTime.UtcNow;
                await scopedDb.SaveChangesAsync();
                scopedLogger.LogInformation("SmileID AML submitted: {JobId} for deal {Ref} result={Result}",
                    aml.JobId, tx.DealReference, aml.ResultCode);
            }
            else
            {
                scopedLogger.LogWarning("SmileID AML submission failed for deal {Ref}", tx.DealReference);
            }
        }
        catch (Exception ex)
        {
            scopedLogger.LogError(ex, "SubmitKycAndAmlAsync failed for transaction {Id}", transactionId);
        }
    }

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
            await TriggerPayoutAsync(updated);
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
        await TriggerPayoutAsync(tx);
        return tx;
    }

    //  Dispute: buyer rejects within 24hr window
    public async Task<Transaction> RejectItemAsync(Guid txId, string reason, int expectedVersion)
    {
        var tx = await db.Transactions.FindAsync(txId)
            ?? throw new KeyNotFoundException($"Transaction {txId} not found");

        if (tx.Status != TransactionStatus.ItemDelivered)
            throw new InvalidOperationException("Item must be in ItemDelivered status to reject");

        if (tx.InspectionWindowEndsAt.HasValue && DateTime.UtcNow > tx.InspectionWindowEndsAt.Value)
            throw new InvalidOperationException("24-hour inspection window has expired");

        var safeReason = new string(reason.Where(c => c != '\n' && c != '\r').ToArray());
        return await AdvanceStateAsync(txId, TransactionStatus.ItemDelivered,
            TransactionStatus.RequiresRefund, "buyer", $"Buyer rejected item: {safeReason}", expectedVersion);
    }

    public async Task<bool> TriggerPayoutAsync(Transaction tx, string? merchantReferenceOverride = null)
    {
        try
        {
            var merchantRef = merchantReferenceOverride ?? tx.DealReference;
            logger.LogInformation("TriggerPayout: starting for {Ref} seller={SellerId} merchantRef={MerchantRef}",
                tx.DealReference, tx.SellerId, merchantRef);

            // Create a fresh scope so we get a live DbContext.
            await using var scope = scopeFactory.CreateAsyncScope();
            var freshDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Serialize all payout attempts for the same deal at the database level.
            // This prevents two concurrent completion/retry requests from both
            // reaching Ozow before either local payout record is persisted.
            await using var payoutTransaction = await freshDb.Database.BeginTransactionAsync();
            await freshDb.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext({0}))", tx.DealReference);

            var seller = await freshDb.Users.FindAsync(tx.SellerId);
            if (seller is null)
            {
                logger.LogError("TriggerPayout: seller {SellerId} not found for {Ref}", tx.SellerId, tx.DealReference);
                await payoutTransaction.RollbackAsync();
                return false;
            }

            if (seller.IdCheckStatus != KycStatus.Approved ||
                seller.AmlStatus != KycStatus.Approved ||
                seller.LivenessStatus != KycStatus.Approved)
            {
                logger.LogWarning("TriggerPayout: seller verification incomplete for {Ref}. KYC={Kyc} AML={Aml} Liveness={Liveness}",
                    tx.DealReference, seller.IdCheckStatus, seller.AmlStatus, seller.LivenessStatus);
                await payoutTransaction.RollbackAsync();
                return false;
            }

            if (string.IsNullOrWhiteSpace(seller.BankAccountNumber) ||
                string.IsNullOrWhiteSpace(seller.BankBranchCode))
            {
                logger.LogWarning("TriggerPayout: seller {SellerId} has no bank details — payout skipped. Account='{Account}' Branch='{Branch}'",
                    tx.SellerId, seller.BankAccountNumber, seller.BankBranchCode);
                await payoutTransaction.RollbackAsync();
                return false;
            }

            var notifyUrl    = config["Ozow:NotifyUrl"] ?? "";
            var verifyUrl    = config["Ozow:VerifyUrl"] ?? "";
            var encKey       = config["Ozow:AccountNumberDecryptionKey"] ?? "";
            var payoutAmount = tx.ItemValue - tx.SellerFee;
            if (payoutAmount <= 0m)
            {
                logger.LogError(
                    "TriggerPayout: calculated payout amount is not positive for {Ref}. ItemValue={ItemValue} SellerFee={SellerFee} PayoutAmount={PayoutAmount}",
                    tx.DealReference, tx.ItemValue, tx.SellerFee, payoutAmount);
                await payoutTransaction.RollbackAsync();
                return false;
            }

            // A normal completion may only create one active payout for the deal.
            // Explicit admin retries use a distinct merchant reference.
            if (merchantReferenceOverride is null)
            {
                var existingPending = await freshDb.PendingPayouts
                    .AsNoTracking()
                    .AnyAsync(p => p.DealReference == tx.DealReference && !p.Resolved);

                if (existingPending)
                {
                    logger.LogWarning(
                        "TriggerPayout: active payout already exists for {Ref}; refusing duplicate submission",
                        tx.DealReference);
                    await payoutTransaction.RollbackAsync();
                    return true;
                }
            }

            // Recover a provider-side payout that was accepted before a previous
            // local database write completed. Never submit a second payout for the
            // same merchant reference when Ozow already has one.
            //
            // For an admin retry, verify the ORIGINAL deal reference first. The
            // retry reference is intentionally different, so checking only the
            // retry reference could duplicate a payout when Ozow already processed
            // the original request but the local PendingPayout row was lost.
            var recoveryReferences = merchantReferenceOverride is null
                ? new[] { merchantRef }
                : new[] { tx.DealReference, merchantRef };

            foreach (var recoveryReference in recoveryReferences.Distinct(StringComparer.Ordinal))
            {
                var existingPayout = await payoutService.FindExistingPayoutAsync(recoveryReference, payoutAmount);
                if (existingPayout is null)
                    continue;

                // Terminal failures are not recoverable payouts. Do not create a
                // local pending record for a failed/returned/cancelled provider
                // record, otherwise an admin retry would be blocked forever.
                if (existingPayout.Status is 4 or 90 or 99)
                {
                    logger.LogWarning(
                        "TriggerPayout: found terminal failed Ozow payout {PayoutId} for {Ref} via {MerchantReference}; allowing a fresh retry. status={Status} subStatus={SubStatus} error={Error}",
                        existingPayout.PayoutId,
                        tx.DealReference,
                        recoveryReference,
                        existingPayout.Status,
                        existingPayout.SubStatus,
                        existingPayout.ErrorMessage);
                    continue;
                }

                freshDb.PendingPayouts.Add(new PendingPayout
                {
                    PayoutId = existingPayout.PayoutId,
                    DealReference = tx.DealReference,
                    Resolved = existingPayout.Status == 5,
                    ResolvedAt = existingPayout.Status == 5 ? DateTime.UtcNow : null,
                });
                await freshDb.SaveChangesAsync();
                await payoutTransaction.CommitAsync();
                logger.LogWarning(
                    "TriggerPayout: recovered existing Ozow payout {PayoutId} for {Ref} via merchant reference {MerchantReference}; status={Status} subStatus={SubStatus}",
                    existingPayout.PayoutId,
                    tx.DealReference,
                    recoveryReference,
                    existingPayout.Status,
                    existingPayout.SubStatus);
                return true;
            }

            logger.LogInformation("TriggerPayout: dispatching R{Amount} to bank={BankGroupId} notifyUrl={NotifyUrl}",
                payoutAmount, seller.BankGroupId, notifyUrl);

            var payoutId = await payoutService.RequestPayoutAsync(
                merchantRef, payoutAmount,
                seller.BankGroupId, seller.BankAccountNumber,
                seller.BankBranchCode, encKey, notifyUrl, verifyUrl);

            if (payoutId is null)
            {
                logger.LogError("TriggerPayout: Ozow rejected payout for {Ref} merchantRef={MerchantRef}", tx.DealReference, merchantRef);
                await payoutTransaction.RollbackAsync();
                return false;
            }

            logger.LogInformation("TriggerPayout: success PayoutId={PayoutId} Ref={Ref} merchantRef={MerchantRef}",
                payoutId, tx.DealReference, merchantRef);
            freshDb.PendingPayouts.Add(new PendingPayout
            {
                PayoutId = payoutId,
                DealReference = tx.DealReference,
            });
            await freshDb.SaveChangesAsync();
            await payoutTransaction.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TriggerPayout: unhandled exception for {Ref}", tx.DealReference);
            return false;
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