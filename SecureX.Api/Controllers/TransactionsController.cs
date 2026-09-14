using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;
using System.Security.Claims;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/transactions")]
public class TransactionsController(
    AppDbContext db,
    TransactionService txService,
    OzowCollectionService ozowCollection,
    IConfiguration config,
    ILogger<TransactionsController> logger) : ControllerBase
{
    private string CallerEmail => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    // ── GET /api/transactions/{id} ────────────────────────────────────────────
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> GetTransaction(Guid id)
    {
        logger.LogInformation("=== GET TRANSACTION ===");
        logger.LogInformation("TransactionId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .Include(t => t.AuditLogs)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound(new { error = "Transaction not found" });
        }

        logger.LogInformation("Transaction found: {DealReference}, Status: {Status}", tx.DealReference, tx.Status);

        // Check if user has access (buyer or seller)
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("GetTransaction: missing/invalid user claim for {Id}", id);
            return Unauthorized(new { error = "Invalid user" });
        }

        if (tx.BuyerId != userId && tx.SellerId != userId && !User.IsInRole("Admin"))
        {
            logger.LogWarning("Unauthorized access to transaction {Id} by user {UserId}", id, userId);
            return Forbid();
        }

        var response = new
        {
            transaction = MapTransaction(tx),
            auditLog = tx.AuditLogs.OrderBy(a => a.Timestamp).Select(a => new
            {
                a.Id,
                PreviousStatus = a.PreviousStatus?.ToString(),
                NewStatus = a.NewStatus.ToString(),
                a.TriggerActor,
                a.ActionDetails,
                a.Timestamp,
            })
        };

        logger.LogInformation("Transaction details returned for: {DealReference}", tx.DealReference);
        return Ok(response);
    }

    // ── GET /api/transactions/ref/{dealReference} ─────────────────────────────
    [HttpGet("ref/{dealReference}")]
    [Authorize]
    public async Task<IActionResult> GetTransactionByRef(string dealReference)
    {
        logger.LogInformation("=== GET TRANSACTION BY REF ===");
        logger.LogInformation("DealReference: {Ref}", dealReference);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .Include(t => t.AuditLogs)
            .FirstOrDefaultAsync(t => t.DealReference == dealReference);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found for ref: {Ref}", dealReference);
            return NotFound(new { error = "Transaction not found" });
        }

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { error = "Invalid user" });

        if (tx.BuyerId != userId && tx.SellerId != userId && !User.IsInRole("Admin"))
        {
            logger.LogWarning("Unauthorized access to {Ref} by user {UserId}", dealReference, userId);
            return Forbid();
        }

        return Ok(new
        {
            transaction = MapTransaction(tx),
            auditLog = tx.AuditLogs.OrderBy(a => a.Timestamp).Select(a => new
            {
                a.Id,
                PreviousStatus = a.PreviousStatus?.ToString(),
                NewStatus = a.NewStatus.ToString(),
                a.TriggerActor,
                a.ActionDetails,
                a.Timestamp,
            })
        });
    }

    // ── POST /api/transactions ─────────────────────────────────────────────────
    // ── POST /api/transactions/{id}/payment-link ─────────────────────────────
    [HttpPost("{id:guid}/payment-link")]
    public async Task<IActionResult> GetPaymentLink(Guid id)
    {
        logger.LogInformation("=== GET PAYMENT LINK ===");
        logger.LogInformation("TransactionId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound(new { error = "Transaction not found" });
        }

        // Verify buyer KYC and AML are both approved
        if (tx.Buyer is null ||
            tx.Buyer.IdCheckStatus != KycStatus.Approved ||
            tx.Buyer.AmlStatus != KycStatus.Approved)
        {
            logger.LogWarning(
                "Payment link requested for {Ref} but buyer KYC/AML not approved. KYC={Kyc}, AML={Aml}",
                tx.DealReference, tx.Buyer?.IdCheckStatus, tx.Buyer?.AmlStatus);
            return BadRequest(new { error = "Buyer verification is not complete" });
        }

        // Generate Ozow payment link
        const string returnUrl = "https://www.secureexchange.co.za/payment-return";
        var redirectUrl = await ozowCollection.CreatePaymentAsync(
            tx.DealReference,
            tx.TotalCheckoutAmount,
            returnUrl);

        if (string.IsNullOrEmpty(redirectUrl))
        {
            logger.LogError("Failed to generate Ozow payment link for {Ref}", tx.DealReference);
            return StatusCode(500, new { error = "Could not generate payment link" });
        }

        logger.LogInformation("Payment link generated for {Ref}: {Url}", tx.DealReference, redirectUrl);

        return Ok(new
        {
            txId = tx.Id,
            dealReference = tx.DealReference,
            totalAmount = tx.TotalCheckoutAmount,
            sellerId = tx.SellerId,
            sellerEmail = tx.Seller?.Email ?? "",
            redirectUrl
        });
    }

    // ── POST /api/transactions/{id}/start-seller-kyc ─────────────────────────
    [HttpPost("{id:guid}/start-seller-kyc")]
    public async Task<IActionResult> StartSellerKyc(Guid id)
    {
        logger.LogInformation("=== START SELLER KYC ===");
        logger.LogInformation("TransactionId: {Id}", id);

        var tx = await db.Transactions
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx?.Seller is null)
        {
            logger.LogWarning("Seller KYC: transaction or seller not found for {Id}", id);
            return NotFound(new { error = "Transaction or seller not found" });
        }

        var token = await txService.CreateSellerLivenessTokenAsync(tx);

        if (string.IsNullOrEmpty(token))
        {
            logger.LogError("Seller KYC token generation failed for {Ref}", tx.DealReference);
            return StatusCode(500, new { error = "Could not create verification session" });
        }

        var isSandbox = (config["SmileId:BaseUrl"] ?? "").Contains("testapi", StringComparison.OrdinalIgnoreCase);
        var product   = config["SmileId:BiometricProduct"] ?? "biometric_kyc";
        var country   = config["SmileId:Country"]          ?? "ZA";
        var idType    = config["SmileId:IdType"]           ?? "NATIONAL_ID";
        var sandboxId = config["SmileId:SandboxIdNumber"]  ?? "0000000000000";
        var sellerNameParts = tx.Seller.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var givenNames = sellerNameParts.Length > 1
            ? string.Join(' ', sellerNameParts[..^1])
            : tx.Seller.FullName;
        var lastName = sellerNameParts.Length > 1 ? sellerNameParts[^1] : "";

        return Ok(new
        {
            token = token,
            product = product,
            environment = isSandbox ? "sandbox" : "production",
            partnerId = config["SmileId:PartnerId"] ?? "8811",
            callbackUrl = config["SmileId:CallbackUrl"] ?? "",
            userDetails = new
            {
                given_names = givenNames,
                last_name = lastName,
                email = tx.Seller.Email,
                phone_number = tx.Seller.Phone,
            },
            idInfo = new Dictionary<string, object>
            {
                [country] = new Dictionary<string, object>
                {
                    [idType] = new Dictionary<string, string>
                    {
                        ["id_number"] = isSandbox ? sandboxId : (string.IsNullOrWhiteSpace(tx.Seller.IdNumber) ? sandboxId : tx.Seller.IdNumber)
                    }
                }
            },
            partnerParams = new
            {
                internal_reference = tx.Id.ToString(),
                deal_reference = tx.DealReference,
                verification_type = "seller_liveness",
            }
        });
    }

    // ── POST /api/transactions ────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> CreateTransaction([FromBody] CreateTransactionRequest req)
    {
        logger.LogInformation("=== CREATE TRANSACTION ===");
        logger.LogInformation(
            "Item: {Item}, Value: {Value}, ServiceType: {ServiceType}",
            req.ItemTitle,
            req.ItemValue,
            req.ServiceType);

        logger.LogInformation(
            "Buyer: {BuyerEmail}, Seller: {SellerEmail}",
            req.BuyerEmail,
            req.SellerEmail);

        logger.LogInformation("Caller: {Caller}", CallerEmail);

        try
        {
            var transaction = await txService.CreateAsync(req);

            // Fire-and-forget KYC/AML submission
            var buyerIdNumber = req.BuyerIdNumber;
            _ = Task.Run(async () =>
            {
                try
                {
                    await txService.SubmitKycAndAmlAsync(transaction.Id, buyerIdNumber);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background KYC/AML submission failed for {DealReference}", transaction.DealReference);
                }
            });

            logger.LogInformation(
                "Transaction created: {DealReference}, Id: {Id}",
                transaction.DealReference,
                transaction.Id);

            return Ok(new
            {
                transactionId = transaction.Id,
                dealReference = transaction.DealReference,
                status = transaction.Status.ToString(),
                redirectUrl = $"/transaction/{transaction.Id}"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to create transaction for buyer {BuyerEmail}",
                req.BuyerEmail);

            return BadRequest(new
            {
                error = ex.Message
            });
        }
    }

    // ── POST /api/transactions/{id}/accept ────────────────────────────────────
    [HttpPost("{id:guid}/accept")]
    [Authorize]
    public async Task<IActionResult> AcceptItem(Guid id)
    {
        logger.LogInformation("=== ACCEPT ITEM ===");
        logger.LogInformation("TransactionId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound(new { error = "Transaction not found" });
        }

        if (tx.BuyerId != userId)
        {
            logger.LogWarning("User {UserId} is not the buyer for transaction {Id}", userId, id);
            return Forbid();
        }

        if (tx.Status != TransactionStatus.ItemDelivered)
        {
            logger.LogWarning("Cannot accept item: Transaction status is {Status}, expected ItemDelivered", tx.Status);
            return BadRequest(new { error = $"Transaction must be in 'ItemDelivered' status (current: {tx.Status})" });
        }

        try
        {
            var updated = await txService.AdvanceStateAsync(
                id,
                TransactionStatus.ItemDelivered,
                TransactionStatus.Completed,
                CallerEmail,
                "Buyer accepted item",
                tx.Version);

            // Trigger payout
            _ = txService.TriggerPayoutAsync(updated);

            logger.LogInformation("Item accepted for transaction: {DealReference}", tx.DealReference);

            return Ok(new
            {
                transactionId = updated.Id,
                dealReference = updated.DealReference,
                status = updated.Status.ToString(),
                message = "Item accepted, payout initiated"
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to accept item: {Error}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST /api/transactions/{id}/reject ────────────────────────────────────
    [HttpPost("{id:guid}/reject")]
    [Authorize]
    public async Task<IActionResult> RejectItem(Guid id, [FromBody] RejectItemRequest req)
    {
        logger.LogInformation("=== REJECT ITEM ===");
        logger.LogInformation("TransactionId: {Id}, Reason: {Reason}", id, req.Reason);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound(new { error = "Transaction not found" });
        }

        if (tx.BuyerId != userId)
        {
            logger.LogWarning("User {UserId} is not the buyer for transaction {Id}", userId, id);
            return Forbid();
        }

        if (tx.Status != TransactionStatus.ItemDelivered)
        {
            logger.LogWarning("Cannot reject item: Transaction status is {Status}, expected ItemDelivered", tx.Status);
            return BadRequest(new { error = $"Transaction must be in 'ItemDelivered' status (current: {tx.Status})" });
        }

        try
        {
            var updated = await txService.AdvanceStateAsync(
                id,
                TransactionStatus.ItemDelivered,
                TransactionStatus.RequiresRefund,
                CallerEmail,
                $"Buyer rejected item: {req.Reason}",
                tx.Version);

            logger.LogInformation("Item rejected for transaction: {DealReference}, Reason: {Reason}", tx.DealReference, req.Reason);

            return Ok(new
            {
                transactionId = updated.Id,
                dealReference = updated.DealReference,
                status = updated.Status.ToString(),
                message = "Item rejected, awaiting dispute resolution"
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to reject item: {Error}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST /api/transactions/{id}/confirm-delivery ──────────────────────────
    [HttpPost("{id:guid}/confirm-delivery")]
    [Authorize]
    public async Task<IActionResult> ConfirmDelivery(Guid id)
    {
        logger.LogInformation("=== CONFIRM DELIVERY ===");
        logger.LogInformation("TransactionId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound(new { error = "Transaction not found" });
        }

        if (tx.SellerId != userId)
        {
            logger.LogWarning("User {UserId} is not the seller for transaction {Id}", userId, id);
            return Forbid();
        }

        if (tx.Status != TransactionStatus.LogisticsPending)
        {
            logger.LogWarning("Cannot confirm delivery: Transaction status is {Status}, expected LogisticsPending", tx.Status);
            return BadRequest(new { error = $"Transaction must be in 'LogisticsPending' status (current: {tx.Status})" });
        }

        try
        {
            var updated = await txService.AdvanceStateAsync(
                id,
                TransactionStatus.LogisticsPending,
                TransactionStatus.ItemDelivered,
                CallerEmail,
                "Seller confirmed delivery",
                tx.Version);

            logger.LogInformation("Delivery confirmed for transaction: {DealReference}", tx.DealReference);

            return Ok(new
            {
                transactionId = updated.Id,
                dealReference = updated.DealReference,
                status = updated.Status.ToString(),
                inspectionWindowEndsAt = updated.InspectionWindowEndsAt,
                message = "Delivery confirmed, inspection window started"
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to confirm delivery: {Error}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST /api/transactions/{id}/mark-as-shipped ───────────────────────────
    [HttpPost("{id:guid}/mark-as-shipped")]
    [Authorize]
    public async Task<IActionResult> MarkAsShipped(Guid id)
    {
        logger.LogInformation("=== MARK AS SHIPPED ===");
        logger.LogInformation("TransactionId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound(new { error = "Transaction not found" });
        }

        if (tx.SellerId != userId)
        {
            logger.LogWarning("User {UserId} is not the seller for transaction {Id}", userId, id);
            return Forbid();
        }

        if (tx.Status != TransactionStatus.FundsSecured)
        {
            logger.LogWarning("Cannot mark as shipped: Transaction status is {Status}, expected FundsSecured", tx.Status);
            return BadRequest(new { error = $"Transaction must be in 'FundsSecured' status (current: {tx.Status})" });
        }

        try
        {
            var updated = await txService.AdvanceStateAsync(
                id,
                TransactionStatus.FundsSecured,
                TransactionStatus.LogisticsPending,
                CallerEmail,
                "Seller marked item as shipped",
                tx.Version);

            logger.LogInformation("Item marked as shipped for transaction: {DealReference}", tx.DealReference);

            return Ok(new
            {
                transactionId = updated.Id,
                dealReference = updated.DealReference,
                status = updated.Status.ToString(),
                message = "Item marked as shipped"
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to mark as shipped: {Error}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── GET /api/transactions ──────────────────────────────────────────────────
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> GetUserTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int size = 20)
    {
        logger.LogInformation("=== GET USER TRANSACTIONS ===");
        logger.LogInformation("Page: {Page}, Size: {Size}", page, size);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        var query = db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .Where(t => t.BuyerId == userId || t.SellerId == userId);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        logger.LogInformation("GET USER TRANSACTIONS: Found {Total} total for user {UserId}", total, userId);

        return Ok(new
        {
            total,
            page,
            size,
            pages = (int)Math.Ceiling((double)total / size),
            items = items.Select(MapTransaction)
        });
    }

    // ── Mappers ───────────────────────────────────────────────────────────────
    private static object MapTransaction(Transaction t) => new
    {
        t.Id,
        t.DealReference,
        Status = t.Status.ToString(),
        t.ItemTitle,
        t.ItemDescription,
        t.SellerLocation,
        t.ItemValue,
        t.PlatformFee,
        t.BuyerFee,
        t.SellerFee,
        t.TotalCheckoutAmount,
        ServiceType = t.ServiceType.ToString(),
        t.Version,
        t.CreatedAt,
        t.InspectionWindowEndsAt,
        Buyer = t.Buyer is null ? null : new
        {
            t.Buyer.Id,
            t.Buyer.FullName,
            t.Buyer.Email,
            t.Buyer.Phone,
            IdCheckStatus = t.Buyer.IdCheckStatus.ToString(),
            AmlStatus = t.Buyer.AmlStatus.ToString(),
            LivenessStatus = t.Buyer.LivenessStatus.ToString()
        },
        Seller = t.Seller is null ? null : new
        {
            t.Seller.Id,
            t.Seller.FullName,
            t.Seller.Email,
            t.Seller.Phone,
            IdCheckStatus = t.Seller.IdCheckStatus.ToString(),
            AmlStatus = t.Seller.AmlStatus.ToString(),
            LivenessStatus = t.Seller.LivenessStatus.ToString()
        }
    };
}