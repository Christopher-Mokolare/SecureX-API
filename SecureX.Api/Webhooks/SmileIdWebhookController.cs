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

        // Try to find jobId in several places:
        // 1. Job-ID header (SmileID sends this)
        // 2. payload.job_id (top-level)
        // 3. payload.partner_params.job_id (nested)
        var jobId = Request.Headers["Job-ID"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(jobId))
            jobId = GetString(payload, "job_id");

        if (string.IsNullOrWhiteSpace(jobId))
        {
            var ppForJobId = GetProperty(payload, "partner_params") ?? GetProperty(payload, "PartnerParams");
            if (ppForJobId.HasValue)
                jobId = GetString(ppForJobId.Value, "job_id");
        }

        // Correlate by SmileID's Job-ID header, with partner metadata as a fallback.
        string? dealReference = null;
        var partnerParams = GetProperty(payload, "partner_params") ?? GetProperty(payload, "PartnerParams");
        if (partnerParams.HasValue)
            dealReference = GetString(partnerParams.Value, "deal_reference");

        var verificationType = partnerParams.HasValue
            ? GetString(partnerParams.Value, "verification_type")
            : null;
        var internalReference = partnerParams.HasValue
            ? GetString(partnerParams.Value, "internal_reference")
            : null;

        if (verificationType == "seller_liveness")
        {
            var sellerTransaction = Guid.TryParse(internalReference, out var txId)
                ? await db.Transactions.Include(t => t.Seller).FirstOrDefaultAsync(t => t.Id == txId)
                : await db.Transactions.Include(t => t.Seller)
                    .FirstOrDefaultAsync(t => t.DealReference == dealReference);

            if (sellerTransaction?.Seller is null)
            {
                logger.LogWarning("SmileID seller liveness webhook: transaction not found for {Ref}", dealReference?.Replace("\n", "").Replace("\r", ""));
                return Ok();
            }

            switch (status)
            {
                case "clear":
                    sellerTransaction.Seller.IdCheckStatus = KycStatus.Approved;
                    sellerTransaction.Seller.LivenessStatus = KycStatus.Approved;
                    break;
                case "attention":
                    sellerTransaction.Seller.IdCheckStatus = KycStatus.Approved;
                    sellerTransaction.Seller.LivenessStatus = KycStatus.Approved;
                    logger.LogInformation("SmileID seller liveness attention (approved) for deal {Ref}", dealReference?.Replace("\n", "").Replace("\r", ""));
                    break;
                case "block":
                    sellerTransaction.Seller.IdCheckStatus = KycStatus.Failed;
                    sellerTransaction.Seller.LivenessStatus = KycStatus.Failed;
                    break;
                case "error":
                    logger.LogError("SmileID seller liveness error for deal {Ref}", dealReference?.Replace("\n", "").Replace("\r", ""));
                    return Ok();
                default:
                    logger.LogWarning("SmileID seller liveness unknown status '{Status}' for deal {Ref}", status?.Replace("\n", "").Replace("\r", ""), dealReference?.Replace("\n", "").Replace("\r", ""));
                    return Ok();
            }

            sellerTransaction.Seller.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Ok();
        }

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
            logger.LogWarning("SmileID webhook: no deal_reference in partner_params. JobId={JobId}", jobId?.Replace("\n", "").Replace("\r", ""));
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
            logger.LogWarning("SmileID webhook: buyer not found for deal {Ref}", dealReference?.Replace("\n", "").Replace("\r", ""));
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
            logger.LogInformation("SmileID AML result for deal {Ref}: {ResultCode}", dealReference?.Replace("\n", "").Replace("\r", ""), amlResultCode?.Replace("\n", "").Replace("\r", ""));
            return Ok();
        }

        switch (status)
        {
            case "clear":
                buyer.IdCheckStatus = KycStatus.Approved;
                logger.LogInformation("SmileID KYC clear for deal {Ref} buyer {BuyerId}", dealReference?.Replace("\n", "").Replace("\r", ""), buyer.Id);
                break;

            case "attention":
                buyer.IdCheckStatus = KycStatus.Approved;
                logger.LogInformation("SmileID KYC attention (approved with flags) for deal {Ref} buyer {BuyerId}", dealReference?.Replace("\n", "").Replace("\r", ""), buyer.Id);
                break;

            case "block":
                var reason = GetString(payload, "reason") ?? "blocked";
                buyer.IdCheckStatus = KycStatus.Failed;
                logger.LogWarning("SmileID KYC blocked for deal {Ref}: {Reason}", dealReference?.Replace("\n", "").Replace("\r", ""), reason?.Replace("\n", "").Replace("\r", ""));
                break;

            case "error":
                logger.LogError("SmileID KYC error for deal {Ref} — status remains Pending", dealReference?.Replace("\n", "").Replace("\r", ""));
                break; // leave as Pending so it can be retried

            default:
                logger.LogWarning("SmileID webhook: unknown status '{Status}' for deal {Ref}", status?.Replace("\n", "").Replace("\r", ""), dealReference?.Replace("\n", "").Replace("\r", ""));
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
        if (element.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(element.GetString() ?? "");
                return GetString(parsed.RootElement, name);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var property = GetProperty(element, name);
        return property.HasValue
            ? property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString()
            : null;
    }
}
