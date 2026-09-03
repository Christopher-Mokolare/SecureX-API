using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using System.Security.Claims;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(AppDbContext db) : ControllerBase
{
    private string CallerEmail => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    // ── GET /api/admin/transactions ───────────────────────────────────────────
    // Query params: page, size, status, search (email/ref), fromDate, toDate
    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int size = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        var query = db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<TransactionStatus>(status, true, out var parsedStatus))
            query = query.Where(t => t.Status == parsedStatus);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t =>
                t.DealReference.Contains(search) ||
                (t.Buyer != null && t.Buyer.Email.Contains(search)) ||
                (t.Seller != null && t.Seller.Email.Contains(search)));

        if (fromDate.HasValue)
            query = query.Where(t => t.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(t => t.CreatedAt <= toDate.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return Ok(new
        {
            total,
            page,
            size,
            pages = (int)Math.Ceiling((double)total / size),
            items = items.Select(MapTransaction),
        });
    }

    // ── GET /api/admin/users ──────────────────────────────────────────────────
    // Query params: page, size, search (email/name), kycStatus, suspended
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int size = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? kycStatus = null,
        [FromQuery] bool? suspended = null)
    {
        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        var query = db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.Email.Contains(search) || u.FullName.Contains(search));

        if (!string.IsNullOrWhiteSpace(kycStatus) &&
            Enum.TryParse<KycStatus>(kycStatus, true, out var parsedKyc))
            query = query.Where(u => u.IdCheckStatus == parsedKyc);

        if (suspended.HasValue)
            query = query.Where(u => u.IsSuspended == suspended.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return Ok(new
        {
            total,
            page,
            size,
            pages = (int)Math.Ceiling((double)total / size),
            items = items.Select(MapUser),
        });
    }

    // ── PATCH /api/admin/users/{id}/kyc ───────────────────────────────────────
    [HttpPatch("users/{id:guid}/kyc")]
    public async Task<IActionResult> OverrideKyc(Guid id, [FromBody] KycOverrideRequest req)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        if (req.IdCheckStatus.HasValue) user.IdCheckStatus = req.IdCheckStatus.Value;
        if (req.AmlStatus.HasValue) user.AmlStatus = req.AmlStatus.Value;
        if (req.LivenessStatus.HasValue) user.LivenessStatus = req.LivenessStatus.Value;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        db.AuditLogs.Add(new AuditLog
        {
            TransactionId = Guid.Empty,
            NewStatus = TransactionStatus.PaymentPending, // placeholder — not a tx action
            TriggerActor = CallerEmail,
            ActionDetails = $"Admin KYC override on user {id}: IdCheck={req.IdCheckStatus}, AML={req.AmlStatus}, Liveness={req.LivenessStatus}",
        });
        await db.SaveChangesAsync();

        return Ok(MapUser(user));
    }

    // ── PATCH /api/admin/users/{id}/suspend ───────────────────────────────────
    [HttpPatch("users/{id:guid}/suspend")]
    public async Task<IActionResult> SuspendUser(Guid id, [FromBody] SuspendRequest req)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        user.IsSuspended = req.Suspended;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new { userId = id, suspended = user.IsSuspended });
    }

    // ── GET /api/admin/stats ──────────────────────────────────────────────────
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var txStats = await db.Transactions
            .GroupBy(t => t.Status)
            .Select(g => new { status = g.Key.ToString(), count = g.Count() })
            .ToListAsync();

        var feeTotal = await db.Transactions
            .Where(t => t.Status == TransactionStatus.Completed)
            .SumAsync(t => t.PlatformFee);

        var disputeCount = await db.Transactions
            .CountAsync(t => t.Status == TransactionStatus.RequiresRefund);

        var userCount = await db.Users.CountAsync();

        return Ok(new { transactionsByStatus = txStats, totalFeesCollected = feeTotal, openDisputes = disputeCount, totalUsers = userCount });
    }

    private static object MapTransaction(Transaction t) => new
    {
        t.Id,
        t.DealReference,
        Status = t.Status.ToString(),
        t.ItemTitle,
        t.ItemValue,
        t.PlatformFee,
        t.TotalCheckoutAmount,
        ServiceType = t.ServiceType.ToString(),
        t.CreatedAt,
        Buyer = t.Buyer is null ? null : new { t.Buyer.Id, t.Buyer.FullName, t.Buyer.Email, IdCheckStatus = t.Buyer.IdCheckStatus.ToString(), AmlStatus = t.Buyer.AmlStatus.ToString() },
        Seller = t.Seller is null ? null : new { t.Seller.Id, t.Seller.FullName, t.Seller.Email, LivenessStatus = t.Seller.LivenessStatus.ToString(), IdCheckStatus = t.Seller.IdCheckStatus.ToString() },
    };

    private static object MapUser(User u) => new
    {
        u.Id,
        u.FullName,
        u.Email,
        u.Phone,
        u.IsAdmin,
        u.IsSuspended,
        IdCheckStatus = u.IdCheckStatus.ToString(),
        AmlStatus = u.AmlStatus.ToString(),
        LivenessStatus = u.LivenessStatus.ToString(),
        BankVerificationStatus = u.BankVerificationStatus.ToString(),
        u.CreatedAt,
    };
}

public class KycOverrideRequest
{
    public KycStatus? IdCheckStatus { get; set; }
    public KycStatus? AmlStatus { get; set; }
    public KycStatus? LivenessStatus { get; set; }
}

public class SuspendRequest
{
    public bool Suspended { get; set; }
}
