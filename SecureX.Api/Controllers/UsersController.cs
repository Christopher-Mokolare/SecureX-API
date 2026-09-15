using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Security;
using SecureX.Api.Services;
using System.Security.Claims;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController(
    AppDbContext db,
    SmileIdService smileIdService,
    OzowCollectionService ozow,
    IConfiguration config,
    ILogger<UsersController> logger) : ControllerBase
{
    private string CallerEmail => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    // ── GET /api/users/{id} ────────────────────────────────────────────────────
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> GetUser(Guid id)
    {
        logger.LogInformation("=== GET USER ===");
        logger.LogInformation("UserId: {Id}", id);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            logger.LogWarning("User not found: {Id}", id);
            return NotFound(new { error = "User not found" });
        }

        // Users can only view their own profile unless they're admin
        if (userId != id && !User.IsInRole("Admin"))
        {
            logger.LogWarning("Unauthorized access to user {Id} by user {UserId}", id, userId);
            return Forbid();
        }

        logger.LogInformation("User found: {Email}", user.Email);

        return Ok(MapUser(user));
    }

    // ── POST /api/users/bank-details ──────────────────────────────────────────
    [HttpPost("bank-details")]
    [DealToken("seller")]
    public async Task<IActionResult> UpdateBankDetails([FromBody] BankDetailsRequest req)
    {
        logger.LogInformation("=== UPDATE BANK DETAILS ===");
        logger.LogInformation("Caller: {Caller}", CallerEmail);
        logger.LogInformation(
            "Bank details request shape: accountLen={AccountLen} branchLen={BranchLen} bank={Bank} hasId={HasId}",
            req.AccountNumber?.Length ?? 0,
            req.BranchCode?.Length ?? 0,
            req.BankGroupId ?? "(null)",
            !string.IsNullOrWhiteSpace(req.IdNumber));

        // Deal-token path: seller is identified by the token's transaction.
        var dealClaims = DealTokenAttribute.GetClaims(HttpContext);
        if (dealClaims is null)
        {
            logger.LogWarning("UpdateBankDetails: no deal claims present");
            return Unauthorized(new { error = "Invalid user" });
        }

        var tx = await db.Transactions
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == dealClaims.TxId);

        if (tx?.Seller is null)
        {
            logger.LogWarning("UpdateBankDetails: transaction or seller not found for {TxId}", dealClaims.TxId);
            return NotFound(new { error = "Transaction or seller not found" });
        }

        // Guard: token must be for the seller of this transaction
        if (tx.SellerId == Guid.Empty)
        {
            logger.LogWarning("UpdateBankDetails: transaction {TxId} has no seller", dealClaims.TxId);
            return BadRequest(new { error = "Transaction has no seller" });
        }

        var user = tx.Seller;

        user.BankAccountNumber = req.AccountNumber;
        user.BankBranchCode = req.BranchCode;
        user.BankGroupId = req.BankGroupId;
        user.IdNumber = req.IdNumber;
        user.BankVerificationStatus = KycStatus.Pending;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        logger.LogInformation("Bank details updated for user: {UserId}", user.Id);

        return Ok(new { message = "Bank details updated successfully" });
    }

    // ── POST /api/users/kyc ────────────────────────────────────────────────────
    [HttpPost("kyc")]
    [Authorize]
    public async Task<IActionResult> StartKyc()
    {
        logger.LogInformation("=== START KYC ===");
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            logger.LogWarning("User not found: {UserId}", userId);
            return NotFound(new { error = "User not found" });
        }

        // Find a transaction for this user
        var tx = await db.Transactions
            .Where(t => t.BuyerId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (tx is null)
        {
            logger.LogWarning("No transaction found for user: {UserId}", userId);
            return BadRequest(new { error = "No transaction found for this user" });
        }

        var smileBaseUrl = config["SmileId:BaseUrl"] ?? "";
        var isSandbox = smileBaseUrl.Contains("testapi", StringComparison.OrdinalIgnoreCase) ||
                        smileBaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
        var kycIdNumber = isSandbox ? "0000000000000" : user.IdNumber;

        var jobId = await smileIdService.SubmitEnhancedKycAsync(
            user.FullName, kycIdNumber, user.Email, user.Phone, tx.DealReference);

        if (jobId is null)
        {
            logger.LogError("SmileID KYC submission failed for user: {UserId}", userId);
            return StatusCode(502, new { error = "KYC submission failed" });
        }

        user.SmileIdJobId = jobId;
        user.IdCheckStatus = KycStatus.Pending;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("KYC started for user: {UserId}, JobId: {JobId}", userId, jobId);

        return Ok(new { jobId, message = "KYC verification initiated" });
    }

    // ── GET /api/users/me ─────────────────────────────────────────────────────
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetMe()
    {
        logger.LogInformation("=== GET ME ===");
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            logger.LogWarning("Unauthorized: No valid user ID in token");
            return Unauthorized(new { error = "Invalid user" });
        }

        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            logger.LogWarning("User not found: {UserId}", userId);
            return NotFound(new { error = "User not found" });
        }

        logger.LogInformation("User profile returned for: {Email}", user.Email);

        return Ok(MapUser(user));
    }

    // ── GET /api/users ─────────────────────────────────────────────────────────
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllUsers(
        [FromQuery] int page = 1,
        [FromQuery] int size = 20,
        [FromQuery] string? search = null)
    {
        logger.LogInformation("=== GET ALL USERS (Admin) ===");
        logger.LogInformation("Page: {Page}, Size: {Size}, Search: {Search}", page, size, search);
        logger.LogInformation("Caller: {Caller}", CallerEmail);

        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        var query = db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.Email.Contains(search) || u.FullName.Contains(search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        logger.LogInformation("GET ALL USERS: Found {Total} total", total);

        return Ok(new
        {
            total,
            page,
            size,
            pages = (int)Math.Ceiling((double)total / size),
            items = items.Select(MapUser)
        });
    }

    // ── Mappers ───────────────────────────────────────────────────────────────
    private static object MapUser(User u) => new
    {
        u.Id,
        u.FullName,
        u.Email,
        u.Phone,
        u.IsAdmin,
        u.IsSuspended,
        BankVerificationStatus = u.BankVerificationStatus.ToString(),
        IdCheckStatus = u.IdCheckStatus.ToString(),
        AmlStatus = u.AmlStatus.ToString(),
        LivenessStatus = u.LivenessStatus.ToString(),
        u.CreatedAt
    };

    // GET /api/users/banks
    [HttpGet("banks")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBanks()
    {
        logger.LogInformation("=== GET BANKS ===");

        var banks = await ozow.GetBanksAsync();

        if (banks.Count == 0)
            logger.LogWarning("[GetBanks] Ozow returned 0 banks");
        else
            logger.LogInformation("[GetBanks] returned {Count} banks", banks.Count);

        return Ok(banks);
    }
}