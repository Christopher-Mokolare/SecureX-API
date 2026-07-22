using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/transactions")]
[Microsoft.AspNetCore.Authorization.Authorize]
public class TransactionsController(TransactionService txService, AppDbContext db, OzowCollectionService collectionService) : ControllerBase
{
    // ── POST /api/transactions — submit deal form ────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTransactionRequest req)
    {
        if (req.ItemValue <= 0) return BadRequest(new ErrorResponse { Error = "Item value must be greater than 0" });
        if (string.IsNullOrEmpty(req.BuyerEmail)) return BadRequest(new ErrorResponse { Error = "Buyer email is required" });
        if (string.IsNullOrEmpty(req.SellerEmail)) return BadRequest(new ErrorResponse { Error = "Seller email is required" });
        if (req.BuyerEmail == req.SellerEmail) return BadRequest(new ErrorResponse { Error = "Buyer and seller cannot be the same person" });

        var tx = await txService.CreateAsync(req);

        var redirectUrl = await collectionService.CreatePaymentAsync(tx.DealReference, tx.TotalCheckoutAmount);
        if (redirectUrl is null)
            return StatusCode(502, new ErrorResponse { Error = "Transaction created but failed to generate payment link" });

        var response = Map(tx);
        response.PaymentRedirectUrl = redirectUrl;
        return Ok(response);
    }

    // ── GET /api/transactions/{id} ───────────────────────────────────────────
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id);
        return tx is null ? NotFound() : Ok(Map(tx));
    }

    // ── GET /api/transactions/ref/{dealReference} ────────────────────────────
    [HttpGet("ref/{dealReference}")]
    public async Task<IActionResult> GetByRef(string dealReference)
    {
        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.DealReference == dealReference);
        return tx is null ? NotFound() : Ok(Map(tx));
    }

    // ── POST /api/transactions/{id}/start-logistics ─────────────────────────
    [HttpPost("{id:guid}/start-logistics")]
    public async Task<IActionResult> StartLogistics(Guid id, [FromBody] AdvanceStateRequest req)
    {
        try
        {
            var tx = await txService.AdvanceStateAsync(id,
                TransactionStatus.FundsSecured, TransactionStatus.LogisticsPending,
                req.Actor, "Seller confirmed delivery arranged — logistics in progress", req.ExpectedVersion);
            return Ok(Map(tx));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new ErrorResponse { Error = "Transaction was modified concurrently. Refresh and retry." }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse { Error = ex.Message }); }
    }

    // ── POST /api/transactions/{id}/mark-delivered ───────────────────────────
    [HttpPost("{id:guid}/mark-delivered")]
    public async Task<IActionResult> MarkDelivered(Guid id, [FromBody] AdvanceStateRequest req)
    {
        try
        {
            var tx = await txService.AdvanceStateAsync(id,
                TransactionStatus.LogisticsPending, TransactionStatus.ItemDelivered,
                req.Actor, "Item marked as delivered — 24hr inspection window started", req.ExpectedVersion);
            return Ok(Map(tx));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new ErrorResponse { Error = "Transaction was modified concurrently. Refresh and retry." }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse { Error = ex.Message }); }
    }

    // ── POST /api/transactions/{id}/accept — buyer accepts item ─────────────
    [HttpPost("{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, [FromBody] AdvanceStateRequest req)
    {
        try
        {
            var tx = await txService.CompleteAsync(id, req.Actor, req.ExpectedVersion);
            return Ok(Map(tx));
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new ErrorResponse { Error = "Transaction was modified concurrently. Refresh and retry." }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse { Error = ex.Message }); }
    }

    // ── POST /api/transactions/{id}/reject — buyer rejects within 24hrs ─────
    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectItemRequest req)
    {
        var tx = await db.Transactions.FindAsync(id);
        if (tx is null) return NotFound();

        try
        {
            var updated = await txService.RejectItemAsync(id, req.Reason, tx.Version);
            return Ok(Map(updated));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    // ── POST /api/transactions/{id}/resolve-dispute ──────────────────────────
    [HttpPost("{id:guid}/resolve-dispute")]
    public async Task<IActionResult> ResolveDispute(Guid id, [FromBody] ResolveDisputeRequest req)
    {
        if (req.Decision is not ("release-to-seller" or "refund-to-buyer"))
            return BadRequest(new ErrorResponse { Error = "Decision must be 'release-to-seller' or 'refund-to-buyer'" });
        try
        {
            var tx = await txService.ResolveDisputeAsync(id, req.Decision, req.Actor);
            return Ok(Map(tx));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse { Error = ex.Message }); }
    }

    // ── GET /api/transactions/{id}/audit ─────────────────────────────────────
    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> Audit(Guid id)
    {
        var logs = await db.AuditLogs
            .Where(a => a.TransactionId == id)
            .OrderBy(a => a.Timestamp)
            .ToListAsync();
        return Ok(logs);
    }

    // ── POST /api/transactions/{id}/payment-link ────────────────────────────
    [HttpPost("{id:guid}/payment-link")]
    public async Task<IActionResult> PaymentLink(Guid id)
    {
        var tx = await db.Transactions.FindAsync(id);
        if (tx is null) return NotFound();

        if (tx.Status != TransactionStatus.PaymentPending)
            return BadRequest(new ErrorResponse { Error = $"Expected PaymentPending, got {tx.Status}" });

        var redirectUrl = await collectionService.CreatePaymentAsync(tx.DealReference, tx.TotalCheckoutAmount);
        if (redirectUrl is null)
            return StatusCode(502, new ErrorResponse { Error = "Failed to create Ozow payment" });

        return Ok(new { dealReference = tx.DealReference, totalAmount = tx.TotalCheckoutAmount, redirectUrl });
    }

    // ── GET /api/transactions/fee-preview ────────────────────────────────────
    [HttpGet("fee-preview")]
    public IActionResult FeePreview([FromQuery] decimal itemValue, [FromQuery] ServiceType serviceType,
        [FromQuery] FeePayer feePayer = FeePayer.Buyer)
    {
        if (itemValue <= 0) return BadRequest(new ErrorResponse { Error = "itemValue must be greater than 0" });
        var fee = TransactionService.CalculateFee(itemValue, serviceType);
        var (buyerFee, sellerFee) = TransactionService.SplitFee(fee, feePayer);
        return Ok(new
        {
            itemValue,
            platformFee = fee,
            buyerFee,
            sellerFee,
            totalCheckoutAmount = itemValue + buyerFee,
            sellerPayout = itemValue - sellerFee,
        });
    }

    private static TransactionResponse Map(Transaction t) => new()
    {
        Id = t.Id,
        DealReference = t.DealReference,
        Status = t.Status.ToString(),
        ItemTitle = t.ItemTitle,
        ItemDescription = t.ItemDescription,
        SellerLocation = t.SellerLocation,
        ItemValue = t.ItemValue,
        PlatformFee = t.PlatformFee,
        BuyerFee = t.BuyerFee,
        SellerFee = t.SellerFee,
        TotalCheckoutAmount = t.TotalCheckoutAmount,
        ServiceType = t.ServiceType.ToString(),
        Version = t.Version,
        CreatedAt = t.CreatedAt,
        InspectionWindowEndsAt = t.InspectionWindowEndsAt,
        Buyer = t.Buyer is null ? null : MapUser(t.Buyer),
        Seller = t.Seller is null ? null : MapUser(t.Seller),
    };

    private static UserResponse MapUser(User u) => new()
    {
        Id = u.Id,
        FullName = u.FullName,
        Email = u.Email,
        Phone = u.Phone,
        BankVerificationStatus = u.BankVerificationStatus.ToString(),
    };
}
