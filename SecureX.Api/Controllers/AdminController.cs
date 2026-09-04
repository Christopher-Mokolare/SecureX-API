using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;
using System.Security.Claims;
using System.Text;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(AppDbContext db, TransactionService txService, SmileIdService smileIdService, IConfiguration config) : ControllerBase
{
    private string CallerEmail => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    // ── GET /api/admin/transactions ───────────────────────────────────────────
    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int size = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? fromDate = null,
        [FromQuery] string? toDate = null)
    {
        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        DateTime? from = DateTime.TryParse(fromDate, out var fd) ? fd.ToUniversalTime() : null;
        DateTime? to   = DateTime.TryParse(toDate,   out var td) ? td.ToUniversalTime().AddDays(1) : null;

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

        if (from.HasValue) query = query.Where(t => t.CreatedAt >= from.Value);
        if (to.HasValue)   query = query.Where(t => t.CreatedAt <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return Ok(new
        {
            total, page, size,
            pages = (int)Math.Ceiling((double)total / size),
            items = items.Select(MapTransaction),
        });
    }

    // ── GET /api/admin/transactions/export ────────────────────────────────────
    [HttpGet("transactions/export")]
    public async Task<IActionResult> ExportTransactions(
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? fromDate = null,
        [FromQuery] string? toDate = null)
    {
        DateTime? from = DateTime.TryParse(fromDate, out var fd) ? fd.ToUniversalTime() : null;
        DateTime? to   = DateTime.TryParse(toDate,   out var td) ? td.ToUniversalTime().AddDays(1) : null;

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

        if (from.HasValue) query = query.Where(t => t.CreatedAt >= from.Value);
        if (to.HasValue)   query = query.Where(t => t.CreatedAt <= to.Value);

        var items = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Reference,Item,Seller,Buyer,Value,Fee,Total,Service,Status,Created");
        foreach (var t in items)
            sb.AppendLine($"{t.DealReference},{CsvEscape(t.ItemTitle)},{CsvEscape(t.Seller?.Email)},{CsvEscape(t.Buyer?.Email)},{t.ItemValue},{t.PlatformFee},{t.TotalCheckoutAmount},{t.ServiceType},{t.Status},{t.CreatedAt:yyyy-MM-dd}");

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"transactions-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ── GET /api/admin/transactions/{id} ──────────────────────────────────────
    [HttpGet("transactions/{id:guid}")]
    public async Task<IActionResult> GetTransaction(Guid id)
    {
        var tx = await db.Transactions
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .Include(t => t.AuditLogs)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx is null) return NotFound();

        var payout = await db.PendingPayouts
            .Where(p => p.DealReference == tx.DealReference)
            .OrderByDescending(p => p.SubmittedAt)
            .FirstOrDefaultAsync();

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
            }),
            payout = payout is null ? null : new
            {
                payout.PayoutId,
                payout.Resolved,
                payout.PollCount,
                payout.SubmittedAt,
                payout.ResolvedAt,
            },
        });
    }

    // ── POST /api/admin/transactions/{id}/resolve-dispute ─────────────────────
    [HttpPost("transactions/{id:guid}/resolve-dispute")]
    public async Task<IActionResult> ResolveDispute(Guid id, [FromBody] ResolveDisputeRequest req)
    {
        if (req.Decision is not ("release-to-seller" or "refund-to-buyer"))
            return BadRequest(new ErrorResponse { Error = "Decision must be 'release-to-seller' or 'refund-to-buyer'" });
        try
        {
            var tx = await txService.ResolveDisputeAsync(id, req.Decision, CallerEmail);
            return Ok(MapTransaction(tx));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse { Error = ex.Message }); }
    }

    // ── POST /api/admin/transactions/{id}/advance ─────────────────────────────
    // Allows admin to manually advance a stuck transaction to the next valid status.
    [HttpPost("transactions/{id:guid}/advance")]
    public async Task<IActionResult> AdvanceTransaction(Guid id, [FromBody] AdvanceTransactionRequest req)
    {
        if (!Enum.TryParse<TransactionStatus>(req.ToStatus, true, out var toStatus))
            return BadRequest(new ErrorResponse { Error = $"Unknown status '{req.ToStatus}'" });

        var tx = await db.Transactions.Include(t => t.Seller).Include(t => t.Buyer)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (tx is null) return NotFound();

        // Allowed admin advances per current status
        var allowed = tx.Status switch
        {
            TransactionStatus.PaymentPending   => new[] { TransactionStatus.FundsSecured },
            TransactionStatus.FundsSecured     => new[] { TransactionStatus.LogisticsPending },
            TransactionStatus.LogisticsPending => new[] { TransactionStatus.ItemDelivered },
            TransactionStatus.ItemDelivered    => new[] { TransactionStatus.Completed, TransactionStatus.RequiresRefund },
            TransactionStatus.RequiresRefund   => new[] { TransactionStatus.Completed, TransactionStatus.Refunded },
            _ => Array.Empty<TransactionStatus>()
        };

        if (!allowed.Contains(toStatus))
            return BadRequest(new ErrorResponse { Error = $"Cannot advance from {tx.Status} to {toStatus}" });

        try
        {
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? $"Admin manual advance to {toStatus}" : req.Reason;
            var updated = await txService.AdvanceStateAsync(id, tx.Status, toStatus, CallerEmail, reason, tx.Version);

            if (toStatus == TransactionStatus.Completed)
                _ = txService.TriggerPayoutAsync(updated);

            return Ok(MapTransaction(updated));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse { Error = ex.Message }); }
    }

    // ── POST /api/admin/transactions/{id}/retry-payout ────────────────────────
    [HttpPost("transactions/{id:guid}/retry-payout")]
    public async Task<IActionResult> RetryPayout(Guid id)
    {
        var tx = await db.Transactions.Include(t => t.Seller).FirstOrDefaultAsync(t => t.Id == id);
        if (tx is null) return NotFound();
        if (tx.Status != TransactionStatus.Completed)
            return BadRequest(new ErrorResponse { Error = $"Transaction must be Completed to retry payout (current: {tx.Status})" });

        var stale = await db.PendingPayouts
            .Where(p => p.DealReference == tx.DealReference && !p.Resolved)
            .ToListAsync();
        foreach (var p in stale) { p.Resolved = true; p.ResolvedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();

        var retryRef = $"{tx.DealReference}-R{DateTime.UtcNow:yyMMddHHmmss}";
        await txService.TriggerPayoutAsync(tx, retryRef);
        return Ok(new { dealReference = tx.DealReference, retryReference = retryRef, message = "Payout resubmitted" });
    }

    // ── GET /api/admin/users ──────────────────────────────────────────────────
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

        var query = db.Users.Where(u => !u.IsAdmin).AsQueryable();

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
            total, page, size,
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

        if (Enum.TryParse<KycStatus>(req.IdCheckStatus, true, out var idCheck)) user.IdCheckStatus = idCheck;
        if (Enum.TryParse<KycStatus>(req.AmlStatus,      true, out var aml))     user.AmlStatus     = aml;
        if (Enum.TryParse<KycStatus>(req.LivenessStatus, true, out var liveness)) user.LivenessStatus = liveness;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        db.AuditLogs.Add(new AuditLog
        {
            TransactionId = null,
            NewStatus = TransactionStatus.PaymentPending,
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

        return Ok(MapUser(user));
    }

    // ── GET /api/admin/reconciliation ─────────────────────────────────────────
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] int page = 1, [FromQuery] int size = 30)
    {
        size = Math.Clamp(size, 1, 100);
        var total = await db.ReconciliationReports.CountAsync();
        var items = await db.ReconciliationReports
            .OrderByDescending(r => r.RunAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new { r.Id, r.RunAt, r.ExpectedFloat, r.OzowFloat, r.Discrepancy, r.AlertFired })
            .ToListAsync();
        return Ok(new { total, page, size, items });
    }

    // ── GET /api/admin/payout-failures ────────────────────────────────────────
    [HttpGet("payout-failures")]
    public async Task<IActionResult> PayoutFailures([FromQuery] int page = 1, [FromQuery] int size = 50)
    {
        size = Math.Clamp(size, 1, 200);
        var query = db.PayoutNotifications
            .Where(n => n.Status == 99 || n.Status == 4 || n.Status == 90)
            .OrderByDescending(n => n.CreatedAt);
        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * size)
            .Take(size)
            .Select(n => new { n.Id, n.PayoutId, n.MerchantReference, n.Status, n.SubStatus, n.Reason, n.HashValid, n.Duplicate, n.CreatedAt })
            .ToListAsync();
        return Ok(new { total, page, size, items });
    }

    // ── GET /api/admin/missing-payouts ────────────────────────────────────────
    // Completed transactions that have no pending_payout record at all — seller never got paid.
    [HttpGet("missing-payouts")]
    public async Task<IActionResult> MissingPayouts()
    {
        var completedRefs = await db.Transactions
            .Where(t => t.Status == TransactionStatus.Completed)
            .Include(t => t.Seller)
            .ToListAsync();

        var refsWithPayout = await db.PendingPayouts
            .Select(p => p.DealReference)
            .ToListAsync();

        var missing = completedRefs
            .Where(t => !refsWithPayout.Contains(t.DealReference))
            .Select(t => new
            {
                t.Id,
                t.DealReference,
                t.ItemValue,
                t.SellerFee,
                SellerPayout = t.ItemValue - t.SellerFee,
                SellerEmail = t.Seller?.Email,
                SellerKycComplete = t.Seller != null &&
                    t.Seller.IdCheckStatus == KycStatus.Approved &&
                    t.Seller.AmlStatus == KycStatus.Approved &&
                    t.Seller.LivenessStatus == KycStatus.Approved,
                SellerHasBank = !string.IsNullOrWhiteSpace(t.Seller?.BankAccountNumber),
                t.CreatedAt,
            })
            .ToList();

        return Ok(missing);
    }

    // ── POST /api/admin/users/{id}/retry-kyc ──────────────────────────────────
    // Re-submits SmileID Enhanced KYC for a buyer whose webhook never arrived.
    [HttpPost("users/{id:guid}/retry-kyc")]
    public async Task<IActionResult> RetryKyc(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        if (user.IdCheckStatus == KycStatus.Approved)
            return BadRequest(new ErrorResponse { Error = "User KYC is already Approved" });

        // Find the most recent transaction for this buyer to use as deal reference context
        var tx = await db.Transactions
            .Where(t => t.BuyerId == id)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (tx is null)
            return BadRequest(new ErrorResponse { Error = "No transaction found for this user" });

        var smileBaseUrl = config["SmileId:BaseUrl"] ?? "";
        var isSandbox = smileBaseUrl.Contains("testapi", StringComparison.OrdinalIgnoreCase) ||
                        smileBaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
        var kycIdNumber = isSandbox ? "0000000000000" : user.IdNumber;

        var jobId = await smileIdService.SubmitEnhancedKycAsync(
            user.FullName, kycIdNumber, user.Email, user.Phone, tx.DealReference);

        if (jobId is null)
            return StatusCode(502, new ErrorResponse { Error = "SmileID KYC re-submission failed" });

        user.SmileIdJobId = jobId;
        user.IdCheckStatus = KycStatus.Pending;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        db.AuditLogs.Add(new AuditLog
        {
            TransactionId = null,
            NewStatus = TransactionStatus.PaymentPending,
            TriggerActor = CallerEmail,
            ActionDetails = $"Admin re-triggered SmileID KYC for user {id} — new jobId={jobId}",
        });
        await db.SaveChangesAsync();

        return Ok(new { jobId, message = "KYC re-submitted" });
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

        var userCount = await db.Users.Where(u => !u.IsAdmin).CountAsync();

        var fundsInEscrow = await db.Transactions
            .Where(t => t.Status == TransactionStatus.FundsSecured ||
                        t.Status == TransactionStatus.LogisticsPending ||
                        t.Status == TransactionStatus.ItemDelivered)
            .SumAsync(t => (decimal?)t.TotalCheckoutAmount) ?? 0;

        var pendingPayouts = await db.PendingPayouts.CountAsync(p => !p.Resolved);

        return Ok(new { transactionsByStatus = txStats, totalFeesCollected = feeTotal, openDisputes = disputeCount, totalUsers = userCount, fundsInEscrow, pendingPayouts });
    }

    // ── GET /api/admin/audit ──────────────────────────────────────────────────
    [HttpGet("audit")]
    public async Task<IActionResult> AuditLog(
        [FromQuery] int page = 1,
        [FromQuery] int size = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? fromDate = null,
        [FromQuery] string? toDate = null)
    {
        size = Math.Clamp(size, 1, 200);
        page = Math.Max(1, page);

        DateTime? from = DateTime.TryParse(fromDate, out var fd) ? fd.ToUniversalTime() : null;
        DateTime? to   = DateTime.TryParse(toDate,   out var td) ? td.ToUniversalTime().AddDays(1) : null;

        var query = db.AuditLogs
            .Where(a => a.TransactionId != null)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.TriggerActor.Contains(search) || a.ActionDetails.Contains(search));

        if (from.HasValue) query = query.Where(a => a.Timestamp >= from.Value);
        if (to.HasValue)   query = query.Where(a => a.Timestamp <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(a => new
            {
                a.Id,
                a.TransactionId,
                PreviousStatus = a.PreviousStatus != null ? a.PreviousStatus.ToString() : null,
                NewStatus = a.NewStatus.ToString(),
                a.TriggerActor,
                a.ActionDetails,
                a.Timestamp,
            })
            .ToListAsync();

        return Ok(new { total, page, size, items });
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
            t.Buyer.Id, t.Buyer.FullName, t.Buyer.Email, t.Buyer.Phone,
            IdCheckStatus = t.Buyer.IdCheckStatus.ToString(),
            AmlStatus = t.Buyer.AmlStatus.ToString(),
            LivenessStatus = t.Buyer.LivenessStatus.ToString(),
            BankVerificationStatus = t.Buyer.BankVerificationStatus.ToString(),
            t.Buyer.IsSuspended,
        },
        Seller = t.Seller is null ? null : new
        {
            t.Seller.Id, t.Seller.FullName, t.Seller.Email, t.Seller.Phone,
            IdCheckStatus = t.Seller.IdCheckStatus.ToString(),
            AmlStatus = t.Seller.AmlStatus.ToString(),
            LivenessStatus = t.Seller.LivenessStatus.ToString(),
            BankVerificationStatus = t.Seller.BankVerificationStatus.ToString(),
            t.Seller.IsSuspended,
        },
    };

    private static object MapUser(User u) => new
    {
        u.Id, u.FullName, u.Email, u.Phone,
        u.IsAdmin, u.IsSuspended,
        IdCheckStatus = u.IdCheckStatus.ToString(),
        AmlStatus = u.AmlStatus.ToString(),
        LivenessStatus = u.LivenessStatus.ToString(),
        BankVerificationStatus = u.BankVerificationStatus.ToString(),
        u.CreatedAt,
    };

    private static string CsvEscape(string? s) =>
        s is null ? "" : s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

public class KycOverrideRequest
{
    public string? IdCheckStatus { get; set; }
    public string? AmlStatus { get; set; }
    public string? LivenessStatus { get; set; }
}

public class AdvanceTransactionRequest
{
    public string ToStatus { get; set; } = "";
    public string? Reason { get; set; }
}

public class SuspendRequest { public bool Suspended { get; set; } }
public class ResolveDisputeRequest { public string Decision { get; set; } = ""; }
