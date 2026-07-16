using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/users")]
[Microsoft.AspNetCore.Authorization.Authorize]
public class UsersController(AppDbContext db, ThisIsMeAvsService avsService, IHttpClientFactory httpFactory, IConfiguration config) : ControllerBase
{
    // ── POST /api/users/{id}/bank-details ────────────────────────────────────
    [HttpPost("{id:guid}/bank-details")]
    public async Task<IActionResult> SaveBankDetails(Guid id, [FromBody] BankDetailsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AccountNumber))
            return BadRequest(new ErrorResponse { Error = "AccountNumber is required" });
        if (string.IsNullOrWhiteSpace(req.BranchCode))
            return BadRequest(new ErrorResponse { Error = "BranchCode is required" });
        if (string.IsNullOrWhiteSpace(req.BankGroupId))
            return BadRequest(new ErrorResponse { Error = "BankGroupId is required" });

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        user.BankAccountNumber = req.AccountNumber;
        user.BankBranchCode    = req.BranchCode;
        user.BankGroupId       = req.BankGroupId;

        if (!string.IsNullOrWhiteSpace(req.IdNumber))
            user.IdNumber = req.IdNumber;

        // Run AVS immediately if we have an ID number
        if (!string.IsNullOrWhiteSpace(user.IdNumber))
        {
            var verified = await avsService.VerifyBankAccountAsync(
                user.IdNumber, req.AccountNumber, req.BranchCode);
            user.BankVerificationStatus = verified ? KycStatus.Approved : KycStatus.Failed;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new
        {
            userId = user.Id,
            bankVerificationStatus = user.BankVerificationStatus.ToString()
        });
    }

    // ── GET /api/users/{id} ──────────────────────────────────────────────────
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        return user is null ? NotFound() : Ok(new UserResponse
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            SmileVerificationStatus = user.SmileVerificationStatus.ToString(),
            BankVerificationStatus = user.BankVerificationStatus.ToString(),
        });
    }

    // ── GET /api/users/banks — proxy to Ozow available banks ────────────────
    [HttpGet("banks")]
    public async Task<IActionResult> GetBanks()
    {
        var baseUrl = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";
        var siteCode = config["Ozow:SiteCode"]!;
        var apiKey   = config["Ozow:PayoutApiKey"]!;

        var client = httpFactory.CreateClient("OzowPayout");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/getavailablebanks");
        req.Headers.Add("SiteCode", siteCode);
        req.Headers.Add("ApiKey", apiKey);

        var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            return StatusCode((int)res.StatusCode, new ErrorResponse { Error = $"Ozow banks fetch failed: {raw}" });

        return Content(raw, "application/json");
    }
}
