using Microsoft.AspNetCore.Authorization;
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
    ILogger<TransactionsController> logger) : ControllerBase
{
    private string CallerEmail => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    // ── GET /api/transactions/{id} ────────────────────────────────────────────
    [HttpGet("{id:guid}")]
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
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
        {
            if (tx.BuyerId != userId && tx.SellerId != userId && !User.IsInRole("Admin"))
            {
                logger.LogWarning("Unauthorized access to transaction {Id} by user {UserId}", id, userId);
                return Forbid();
            }
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

    // ── POST /api/transactions ─────────────────────────────────────────────────
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
            t.Buyer.Phone
        },
        Seller = t.Seller is null ? null : new
        {
            t.Seller.Id,
            t.Seller.FullName,
            t.Seller.Email,
            t.Seller.Phone
        }
    };
}