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
public class SmileIdWebhookController(AppDbContext db, SmileIdService smileId, ILogger<SmileIdWebhookController> logger) : ControllerBase
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
        catch { return BadRequest(); }

        // SmileID V3 callbacks carry signature + timestamp in the JSON body
        var signature = payload.TryGetProperty("signature", out var sigProp) ? sigProp.GetString() ?? "" : "";
        var timestamp = payload.TryGetProperty("timestamp", out var tsProp)  ? tsProp.GetString()  ?? "" : "";

        logger.LogInformation("SmileID webhook raw body: {Body}", body);
        logger.LogInformation("SmileID webhook sig={Sig} ts={Ts}", signature, timestamp);

        if (!smileId.VerifyWebhookSignature(signature, timestamp))
        {
            logger.LogWarning("SmileID webhook: invalid signature");
            return Unauthorized();
        }

        var status = payload.TryGetProperty("status", out var s) ? s.GetString() : null;
        var jobId  = payload.TryGetProperty("job_id", out var jid) ? jid.GetString() : null;

        // Correlate via partner_params.deal_reference
        string? dealReference = null;
        if (payload.TryGetProperty("partner_params", out var pp) &&
            pp.TryGetProperty("deal_reference", out var dr))
            dealReference = dr.GetString();

        if (string.IsNullOrEmpty(dealReference))
        {
            logger.LogWarning("SmileID webhook: no deal_reference in partner_params. JobId={JobId}", jobId);
            return Ok(); // ack to prevent retries
        }

        var buyer = await db.Users
            .Join(db.Transactions, u => u.Id, t => t.BuyerId, (u, t) => new { u, t })
            .Where(x => x.t.DealReference == dealReference)
            .Select(x => x.u)
            .FirstOrDefaultAsync();

        if (buyer is null)
        {
            logger.LogWarning("SmileID webhook: buyer not found for deal {Ref}", dealReference);
            return Ok();
        }

        switch (status)
        {
            case "clear":
                buyer.IdCheckStatus = KycStatus.Approved;
                buyer.AmlStatus     = KycStatus.Approved;
                logger.LogInformation("SmileID KYC clear for deal {Ref} buyer {BuyerId}", dealReference, buyer.Id);
                break;

            case "block":
                var reason = payload.TryGetProperty("reason", out var r) ? r.GetString() : "blocked";
                buyer.IdCheckStatus = KycStatus.Failed;
                buyer.AmlStatus     = KycStatus.Failed;
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
}
