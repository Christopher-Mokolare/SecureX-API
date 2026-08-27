using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SecureX.Api.Data;
using SecureX.Api.Models;
using SecureX.Api.Services;

namespace SecureX.Api.Webhooks;

[ApiController]
[Route("api/transactions")]
[IgnoreAntiforgeryToken]
public class SmileIdWebhookController(AppDbContext db, SmileIdService smileId, IConfiguration config, ILogger<SmileIdWebhookController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    // ── POST /api/transactions/kyc-webhook ───────────────────────────────────
    [HttpPost("kyc-webhook")]
    public async Task<IActionResult> KycWebhook()
    {
        string body;
        using (var reader = new System.IO.StreamReader(Request.Body))
            body = await reader.ReadToEndAsync();

        JsonElement payload;
        try { payload = JsonSerializer.Deserialize<JsonElement>(body, _json); }
        catch (JsonException) { return BadRequest(); }

        var signature = Request.Headers["Response-Signature"].FirstOrDefault() ?? "";
        var timestamp = Request.Headers["Response-Timestamp"].FirstOrDefault() ?? "";
        var isSandbox = (config["SmileId:BaseUrl"] ?? "").Contains("testapi", StringComparison.OrdinalIgnoreCase);

        if (!(isSandbox && string.IsNullOrWhiteSpace(signature) && string.IsNullOrWhiteSpace(timestamp)) &&
            !smileId.VerifyWebhookSignature(signature, timestamp))
        {
            logger.LogWarning("SmileID webhook: invalid signature");
            return Unauthorized();
        }

        var status = GetString(payload, "status");
        var jobId = Request.Headers["Job-ID"].FirstOrDefault() ?? GetString(payload, "job_id");

        // Correlate by SmileID's Job-ID header, with partner metadata as a fallback.
        string? dealReference = null;
        var partnerParams = GetProperty(payload, "partner_params") ?? GetProperty(payload, "PartnerParams");
        if (partnerParams.HasValue)
            dealReference = GetString(partnerParams.Value, "deal_reference");

        var amlResultCode = GetString(payload, "ResultCode") ?? GetString(payload, "result_code");
        var amlJobId = partnerParams.HasValue
            ? GetString(partnerParams.Value, "job_id")
            : null;

        User? buyer = null;
        if (!string.IsNullOrWhiteSpace(amlResultCode) && !string.IsNullOrWhiteSpace(amlJobId))
            buyer = await db.Users.FirstOrDefaultAsync(u => u.SmileIdAmlJobId == amlJobId);
        else if (!string.IsNullOrWhiteSpace(jobId))
            buyer = await db.Users.FirstOrDefaultAsync(u => u.SmileIdJobId == jobId);

        if (buyer is null && string.IsNullOrEmpty(dealReference))
        {
            logger.LogWarning("SmileID webhook: no deal_reference in partner_params. JobId={JobId}", jobId);
            return Ok(); // ack to prevent retries
        }

        if (buyer is null)
            buyer = await db.Users
                .Join(db.Transactions, u => u.Id, t => t.BuyerId, (u, t) => new { u, t })
                .Where(x => x.t.DealReference == dealReference)
                .Select(x => x.u)
                .FirstOrDefaultAsync();

        if (buyer is null)
        {
            logger.LogWarning("SmileID webhook: buyer not found for deal {Ref}", dealReference);
            return Ok();
        }

        if (!string.IsNullOrWhiteSpace(amlResultCode))
        {
            buyer.AmlStatus = amlResultCode switch
            {
                "1031" => KycStatus.Approved,
                "1030" => KycStatus.Failed,
                _ => KycStatus.Pending
            };
            buyer.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation("SmileID AML result for deal {Ref}: {ResultCode}", dealReference, amlResultCode);
            return Ok();
        }

        switch (status)
        {
            case "clear":
                buyer.IdCheckStatus = KycStatus.Approved;
                logger.LogInformation("SmileID KYC clear for deal {Ref} buyer {BuyerId}", dealReference, buyer.Id);
                break;

            case "block":
                var reason = GetString(payload, "reason") ?? "blocked";
                buyer.IdCheckStatus = KycStatus.Failed;
                logger.LogWarning("SmileID KYC blocked for deal {Ref}: {Reason}", dealReference, reason);
                break;

            case "error":
                logger.LogError("SmileID KYC error for deal {Ref} — status remains Pending", dealReference);
                break; // leave as Pending so it can be retried

            default:
                logger.LogWarning("SmileID webhook: unknown status '{Status}' for deal {Ref}", status, dealReference);
                break;
        }

        buyer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok();
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        return property.HasValue
            ? property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString()
            : null;
    }
}
