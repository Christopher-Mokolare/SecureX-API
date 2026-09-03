using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/users")]
[Microsoft.AspNetCore.Authorization.Authorize]
[IgnoreAntiforgeryToken]
public class UsersController(AppDbContext db, IHttpClientFactory httpFactory, IConfiguration config) : ControllerBase
{
    // ── POST /api/users/{id}/bank-details ────────────────────────────────────
    // AllowAnonymous: seller arrives via magic link with no JWT session
    [HttpPost("{id:guid}/bank-details")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
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
            BankVerificationStatus = user.BankVerificationStatus.ToString(),
            IdCheckStatus = user.IdCheckStatus.ToString(),
            AmlStatus = user.AmlStatus.ToString(),
            LivenessStatus = user.LivenessStatus.ToString(),
        });
    }

    // ── GET /api/users/banks — proxy to Ozow available banks ────────────────
    // Maps Ozow field names (bankGroupName, universalBranchCode) to FE-expected names
    [HttpGet("banks")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public async Task<IActionResult> GetBanks()
    {
        var baseUrl  = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";
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

        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var mapped = doc.RootElement.EnumerateArray().Select(b => new
        {
            bankGroupId = b.GetProperty("bankGroupId").GetString(),
            bankName    = b.GetProperty("bankGroupName").GetString(),
            branchCode  = b.GetProperty("universalBranchCode").GetString(),
        }).ToList();

        return Ok(mapped);
    }
}
