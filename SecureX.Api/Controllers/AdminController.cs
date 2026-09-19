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
public class AdminController(
    AppDbContext db,
    TransactionService txService,
    SmileIdService smileIdService,
    IConfiguration config,
    ILogger<AdminController> logger,
    AwsCloudWatchLogsService awsLogs) : ControllerBase
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
        logger.LogInformation("=== GET TRANSACTIONS ===");
        logger.LogInformation("Page: {Page}, Size: {Size}, Status: {Status}, Search: {Search}", page, size, status, search);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        DateTime? from = DateTime.TryParse(fromDate, out var fd) ? fd.ToUniversalTime() : null;
        DateTime? to = DateTime.TryParse(toDate, out var td) ? td.ToUniversalTime().AddDays(1) : null;

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
        if (to.HasValue) query = query.Where(t => t.CreatedAt <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        logger.LogInformation("GET TRANSACTIONS: Found {Total} total", total);

        return Ok(new
        {
            total,
            page,
            size,
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
        logger.LogInformation("=== EXPORT TRANSACTIONS ===");
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        DateTime? from = DateTime.TryParse(fromDate, out var fd) ? fd.ToUniversalTime() : null;
        DateTime? to = DateTime.TryParse(toDate, out var td) ? td.ToUniversalTime().AddDays(1) : null;

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
        if (to.HasValue) query = query.Where(t => t.CreatedAt <= to.Value);

        var items = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Reference,Item,Seller,Buyer,Value,Fee,Total,Service,Status,Created");
        foreach (var t in items)
            sb.AppendLine($"{t.DealReference},{CsvEscape(t.ItemTitle)},{CsvEscape(t.Seller?.Email)},{CsvEscape(t.Buyer?.Email)},{t.ItemValue},{t.PlatformFee},{t.TotalCheckoutAmount},{t.ServiceType},{t.Status},{t.CreatedAt:yyyy-MM-dd}");

        logger.LogInformation("EXPORT TRANSACTIONS: Exporting {Count} transactions", items.Count);

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"transactions-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ── GET /api/admin/transactions/{id} ──────────────────────────────────────
    [HttpGet("transactions/{id:guid}")]
    public async Task<IActionResult> GetTransaction(Guid id)
    {
        logger.LogInformation("=== GET TRANSACTION DETAIL ===");
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
            return NotFound();
        }

        var payout = await db.PendingPayouts
            .Where(p => p.DealReference == tx.DealReference)
            .OrderByDescending(p => p.SubmittedAt)
            .FirstOrDefaultAsync();

        logger.LogInformation("Transaction found: {DealReference}, Status: {Status}", tx.DealReference, tx.Status);

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
        logger.LogInformation("=== RESOLVE DISPUTE ===");
        logger.LogInformation("TransactionId: {Id}, Decision: {Decision}", id, req.Decision);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        if (req.Decision is not ("release-to-seller" or "refund-to-buyer"))
        {
            logger.LogWarning("Invalid dispute decision: {Decision}", req.Decision);
            return BadRequest(new ErrorResponse { Error = "Decision must be 'release-to-seller' or 'refund-to-buyer'" });
        }

        try
        {
            var tx = await txService.ResolveDisputeAsync(id, req.Decision, CallerEmail);
            logger.LogInformation("Dispute resolved successfully: {Id}", id);
            return Ok(MapTransaction(tx));
        }
        catch (KeyNotFoundException)
        {
            logger.LogWarning("Transaction not found for dispute: {Id}", id);
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Invalid operation for dispute: {Id}, Error: {Error}", id, ex.Message);
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    // ── POST /api/admin/transactions/{id}/advance ─────────────────────────────
    [HttpPost("transactions/{id:guid}/advance")]
    public async Task<IActionResult> AdvanceTransaction(Guid id, [FromBody] AdvanceTransactionRequest req)
    {
        logger.LogInformation("=== ADVANCE TRANSACTION ===");
        logger.LogInformation("TransactionId: {Id}, ToStatus: {ToStatus}", id, req.ToStatus);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        if (!Enum.TryParse<TransactionStatus>(req.ToStatus, true, out var toStatus))
        {
            logger.LogWarning("Unknown status: {Status}", req.ToStatus);
            return BadRequest(new ErrorResponse { Error = $"Unknown status '{req.ToStatus}'" });
        }

        var tx = await db.Transactions.Include(t => t.Seller).Include(t => t.Buyer)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound();
        }

        var allowed = tx.Status switch
        {
            TransactionStatus.PaymentPending => new[] { TransactionStatus.FundsSecured },
            TransactionStatus.FundsSecured => new[] { TransactionStatus.LogisticsPending },
            TransactionStatus.LogisticsPending => new[] { TransactionStatus.ItemDelivered },
            TransactionStatus.ItemDelivered => new[] { TransactionStatus.Completed, TransactionStatus.RequiresRefund },
            TransactionStatus.RequiresRefund => new[] { TransactionStatus.Completed, TransactionStatus.Refunded },
            _ => Array.Empty<TransactionStatus>()
        };

        if (!allowed.Contains(toStatus))
        {
            logger.LogWarning("Cannot advance from {Current} to {Target}", tx.Status, toStatus);
            return BadRequest(new ErrorResponse { Error = $"Cannot advance from {tx.Status} to {toStatus}" });
        }

        try
        {
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? $"Admin manual advance to {toStatus}" : req.Reason;
            var updated = await txService.AdvanceStateAsync(id, tx.Status, toStatus, CallerEmail, reason, tx.Version);

            if (toStatus == TransactionStatus.Completed)
                _ = txService.TriggerPayoutAsync(updated);

            logger.LogInformation("Transaction advanced: {Id} from {Old} to {New}", id, tx.Status, toStatus);
            return Ok(MapTransaction(updated));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Invalid operation for advance: {Id}, Error: {Error}", id, ex.Message);
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    // ── POST /api/admin/transactions/{id}/retry-payout ────────────────────────
    [HttpPost("transactions/{id:guid}/retry-payout")]
    public async Task<IActionResult> RetryPayout(Guid id)
    {
        logger.LogInformation("=== RETRY PAYOUT ===");
        logger.LogInformation("TransactionId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var tx = await db.Transactions.Include(t => t.Seller).FirstOrDefaultAsync(t => t.Id == id);
        if (tx is null)
        {
            logger.LogWarning("Transaction not found: {Id}", id);
            return NotFound();
        }

        if (tx.Status != TransactionStatus.Completed)
        {
            logger.LogWarning("Cannot retry payout: Transaction not completed. Status: {Status}", tx.Status);
            return BadRequest(new ErrorResponse { Error = $"Transaction must be Completed to retry payout (current: {tx.Status})" });
        }

        var stale = await db.PendingPayouts
            .Where(p => p.DealReference == tx.DealReference && !p.Resolved)
            .ToListAsync();
        foreach (var p in stale) { p.Resolved = true; p.ResolvedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();

        var retryRef = $"{tx.DealReference}-R{DateTime.UtcNow:yyMMddHHmmss}";
        await txService.TriggerPayoutAsync(tx, retryRef);

        logger.LogInformation("Payout retried for: {DealReference}", tx.DealReference);
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
        logger.LogInformation("=== GET USERS ===");
        logger.LogInformation("Page: {Page}, Size: {Size}, Search: {Search}", page, size, search);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

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

        logger.LogInformation("GET USERS: Found {Total} total", total);

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
        logger.LogInformation("=== OVERRIDE KYC ===");
        logger.LogInformation("UserId: {Id}, IdCheck: {IdCheck}, AML: {Aml}, Liveness: {Liveness}", 
            id, req.IdCheckStatus, req.AmlStatus, req.LivenessStatus);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            logger.LogWarning("User not found: {Id}", id);
            return NotFound();
        }

        if (Enum.TryParse<KycStatus>(req.IdCheckStatus, true, out var idCheck)) user.IdCheckStatus = idCheck;
        if (Enum.TryParse<KycStatus>(req.AmlStatus, true, out var aml)) user.AmlStatus = aml;
        if (Enum.TryParse<KycStatus>(req.LivenessStatus, true, out var liveness)) user.LivenessStatus = liveness;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        db.AuditLogs.Add(new AuditLog
        {
            TransactionId = null,
            NewStatus = TransactionStatus.PaymentPending,
            TriggerActor = CallerEmail,
            ActionDetails = $"Admin KYC override on user {id}: IdCheck={req.IdCheckStatus}, AML={req.AmlStatus}, Liveness={req.LivenessStatus}",
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        logger.LogInformation("KYC override successful for user: {Id}", id);

        return Ok(MapUser(user));
    }

    // ── PATCH /api/admin/users/{id}/suspend ───────────────────────────────────
    [HttpPatch("users/{id:guid}/suspend")]
    public async Task<IActionResult> SuspendUser(Guid id, [FromBody] SuspendRequest req)
    {
        logger.LogInformation("=== SUSPEND USER ===");
        logger.LogInformation("UserId: {Id}, Suspended: {Suspended}", id, req.Suspended);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            logger.LogWarning("User not found: {Id}", id);
            return NotFound();
        }

        user.IsSuspended = req.Suspended;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Log to audit log table
        db.AuditLogs.Add(new AuditLog
        {
            TransactionId = null,
            NewStatus = TransactionStatus.PaymentPending,
            TriggerActor = CallerEmail,
            ActionDetails = $"Admin {(req.Suspended ? "suspended" : "unsuspended")} user {id}",
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        logger.LogInformation("User suspension updated: {Id}, Suspended: {Suspended}", id, req.Suspended);

        return Ok(MapUser(user));
    }

    // ── GET /api/admin/reconciliation ─────────────────────────────────────────
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] int page = 1, [FromQuery] int size = 30)
    {
        logger.LogInformation("=== GET RECONCILIATION ===");
        logger.LogInformation("Page: {Page}, Size: {Size}", page, size);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        size = Math.Clamp(size, 1, 100);
        var total = await db.ReconciliationReports.CountAsync();
        var items = await db.ReconciliationReports
            .OrderByDescending(r => r.RunAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new { r.Id, r.RunAt, r.ExpectedFloat, r.OzowFloat, r.Discrepancy, r.AlertFired, r.Status, r.Error })
            .ToListAsync();

        logger.LogInformation("GET RECONCILIATION: Found {Total} total", total);

        return Ok(new { total, page, size, items });
    }

    // ── GET /api/admin/payout-failures ────────────────────────────────────────
    [HttpGet("payout-failures")]
    public async Task<IActionResult> PayoutFailures([FromQuery] int page = 1, [FromQuery] int size = 50)
    {
        logger.LogInformation("=== GET PAYOUT FAILURES ===");
        logger.LogInformation("Page: {Page}, Size: {Size}", page, size);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

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

        logger.LogInformation("GET PAYOUT FAILURES: Found {Total} total", total);

        return Ok(new { total, page, size, items });
    }

    // ── GET /api/admin/missing-payouts ────────────────────────────────────────
    [HttpGet("missing-payouts")]
    public async Task<IActionResult> MissingPayouts()
    {
        logger.LogInformation("=== GET MISSING PAYOUTS ===");
        logger.LogInformation("Caller: {Caller}", CallerEmail);

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

        logger.LogInformation("GET MISSING PAYOUTS: Found {Count} missing", missing.Count);

        return Ok(missing);
    }

    // ── POST /api/admin/users/{id}/retry-kyc ──────────────────────────────────
    [HttpPost("users/{id:guid}/retry-kyc")]
    public async Task<IActionResult> RetryKyc(Guid id)
    {
        logger.LogInformation("=== RETRY KYC ===");
        logger.LogInformation("UserId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            logger.LogWarning("User not found: {Id}", id);
            return NotFound(new ErrorResponse { Error = "User not found" });
        }

        if (user.IdCheckStatus == KycStatus.Approved)
        {
            logger.LogWarning("KYC already approved for user: {Id}", id);
            return BadRequest(new ErrorResponse { Error = "User KYC is already Approved" });
        }

        // Check if SmileID is configured - DON'T auto-approve
        var smileBaseUrl = config["SmileId:BaseUrl"] ?? "";
        var smilePartnerId = config["SmileId:PartnerId"] ?? "";
        var smileApiKey = config["SmileId:ApiKey"] ?? "";
        var isSmileConfigured = !string.IsNullOrEmpty(smileBaseUrl) && 
                                !string.IsNullOrEmpty(smilePartnerId) && 
                                !string.IsNullOrEmpty(smileApiKey);

        if (!isSmileConfigured)
        {
            logger.LogError("SmileID is not configured. Please set SmileId:BaseUrl, SmileId:PartnerId, and SmileId:ApiKey");
            return StatusCode(503, new ErrorResponse { 
                Error = "KYC service is not configured. Please contact the system administrator." 
            });
        }

        // Find the most recent transaction for this buyer
        var tx = await db.Transactions
            .Where(t => t.BuyerId == id)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (tx is null)
        {
            logger.LogWarning("No transaction found for user: {Id}", id);
            return BadRequest(new ErrorResponse { Error = "No transaction found for this user" });
        }

        var isSandbox = smileBaseUrl.Contains("testapi", StringComparison.OrdinalIgnoreCase) ||
                        smileBaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
        var kycIdNumber = isSandbox ? "0000000000000" : user.IdNumber;

        var jobId = await smileIdService.SubmitEnhancedKycAsync(
            user.FullName, kycIdNumber, user.Email, user.Phone, tx.DealReference);

        if (jobId is null)
        {
            logger.LogError("SmileID KYC re-submission failed for user: {Id}", id);
            return StatusCode(502, new ErrorResponse { Error = "SmileID KYC re-submission failed" });
        }

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
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        logger.LogInformation("KYC retry successful for user: {Id}, JobId: {JobId}", id, jobId);

        return Ok(new { jobId, message = "KYC re-submitted" });
    }

    // ── GET /api/admin/stats ──────────────────────────────────────────────────
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        logger.LogInformation("=== GET STATS ===");
        logger.LogInformation("Caller: {Caller}", CallerEmail);

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

        logger.LogInformation("GET STATS: Fees={Fees}, Disputes={Disputes}, Users={Users}", feeTotal, disputeCount, userCount);

        return Ok(new
        {
            transactionsByStatus = txStats,
            totalFeesCollected = feeTotal,
            openDisputes = disputeCount,
            totalUsers = userCount,
            fundsInEscrow,
            pendingPayouts
        });
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
        logger.LogInformation("=== GET AUDIT LOG ===");
        logger.LogInformation("Page: {Page}, Size: {Size}, Search: {Search}", page, size, search);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        size = Math.Clamp(size, 1, 200);
        page = Math.Max(1, page);

        DateTime? from = DateTime.TryParse(fromDate, out var fd) ? fd.ToUniversalTime() : null;
        DateTime? to = DateTime.TryParse(toDate, out var td) ? td.ToUniversalTime().AddDays(1) : null;

        // FIX: Removed .Where(a => a.TransactionId != null) so KYC audit records are included
        var query = db.AuditLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.TriggerActor.Contains(search) || a.ActionDetails.Contains(search));

        if (from.HasValue) query = query.Where(a => a.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(a => a.Timestamp <= to.Value);

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

        logger.LogInformation("GET AUDIT LOG: Found {Total} total", total);

        return Ok(new { total, page, size, items });
    }

    [HttpGet("aws-logs")]
    public async Task<IActionResult> AwsLogs(
        [FromQuery] int hours = 1,
        [FromQuery] string? search = null,
        [FromQuery] string? level = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? nextToken = null)
    {
        hours = Math.Clamp(hours, 1, 24 * 7);
        limit = Math.Clamp(limit, 1, 100);
        var to = DateTime.UtcNow;
        var from = to.AddHours(-hours);

        try
        {
            var result = await awsLogs.GetAsync(from, to, search, level, limit, nextToken, HttpContext.RequestAborted);
            return Ok(new
            {
                items = result.Items,
                nextToken = result.NextToken,
                logGroup = result.LogGroup,
                from = result.From,
                to = result.To,
                region = config["AWS_REGION"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "configured",
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "CloudWatch log query failed for admin {Caller}", CallerEmail);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (Amazon.CloudWatchLogs.Model.ResourceNotFoundException ex)
        {
            logger.LogWarning(ex, "CloudWatch log group unavailable for admin {Caller}", CallerEmail);
            return StatusCode(503, new { error = ex.Message });
        }
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
            LivenessStatus = t.Buyer.LivenessStatus.ToString(),
            BankVerificationStatus = t.Buyer.BankVerificationStatus.ToString(),
            t.Buyer.IsSuspended,
        },
        Seller = t.Seller is null ? null : new
        {
            t.Seller.Id,
            t.Seller.FullName,
            t.Seller.Email,
            t.Seller.Phone,
            IdCheckStatus = t.Seller.IdCheckStatus.ToString(),
            AmlStatus = t.Seller.AmlStatus.ToString(),
            LivenessStatus = t.Seller.LivenessStatus.ToString(),
            BankVerificationStatus = t.Seller.BankVerificationStatus.ToString(),
            t.Seller.IsSuspended,
        },
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
