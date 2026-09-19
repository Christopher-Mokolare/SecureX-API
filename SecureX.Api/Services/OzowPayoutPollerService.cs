using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

/// <summary>
/// Polls Ozow's getpayout endpoint for any payout that has been pending longer than
/// the configured SLA without receiving a terminal webhook notification.
/// This is the "dual-path" backup recommended in the Ozow Payouts integration guide.
/// </summary>
public class OzowPayoutPollerService(IServiceScopeFactory scopeFactory, IConfiguration config,
    ILogger<OzowPayoutPollerService> logger) : BackgroundService
{
    // How often to run the poll loop
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(2);

    // How long to wait before treating a payout as "overdue" (no webhook received)
    private static readonly TimeSpan SlaCutoff = TimeSpan.FromMinutes(5);

    // Terminal statuses — stop polling once reached
    private static readonly HashSet<int> TerminalStatuses = [5, 4, 90, 99];

    // Give up after this many polls (~10 min at 2-min interval) to avoid infinite polling
    private const int MaxPollAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Stagger startup so migrations complete first
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (Exception ex) { logger.LogError(ex, "PayoutPoller: unhandled error"); }

            await Task.Delay(PollingInterval, ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var txService = scope.ServiceProvider.GetRequiredService<TransactionService>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        var cutoff = DateTime.UtcNow - SlaCutoff;
        var overdue = await db.PendingPayouts
            .Where(p => !p.Resolved && p.SubmittedAt <= cutoff)
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        logger.LogInformation("PayoutPoller: checking {Count} overdue payout(s)", overdue.Count);

        var baseUrl  = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";
        var siteCode = config["Ozow:SiteCode"]!;
        var apiKey   = config["Ozow:PayoutApiKey"]!;
        var client   = httpFactory.CreateClient("OzowPayout");

        foreach (var pending in overdue)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/getpayout?payoutId={pending.PayoutId}");
                req.Headers.Add("SiteCode", siteCode);
                req.Headers.Add("ApiKey", apiKey);

                var res = await client.SendAsync(req, ct);
                var raw = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                {
                    logger.LogWarning("PayoutPoller: getpayout returned {Status} for {PayoutId}: {Body}",
                        res.StatusCode, pending.PayoutId, raw);
                    continue;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                int status = 0, subStatus = 0;
                if (root.TryGetProperty("payoutStatus", out var ps))
                {
                    ps.TryGetProperty("status", out var sv);
                    ps.TryGetProperty("subStatus", out var ssv);
                    status    = sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 0;
                    subStatus = ssv.ValueKind == JsonValueKind.Number ? ssv.GetInt32() : 0;
                }

                logger.LogInformation("PayoutPoller: {PayoutId} status={Status} subStatus={SubStatus}",
                    pending.PayoutId, status, subStatus);

                if (status == 5) // PayoutComplete
                    await txService.HandlePayoutCompleteAsync(pending.DealReference, pending.PayoutId);
                else if (TerminalStatuses.Contains(status))
                    logger.LogWarning("PayoutPoller: payout terminal non-complete. PayoutId={PayoutId} status={Status} subStatus={SubStatus} Ref={Ref}",
                        pending.PayoutId, status, subStatus, pending.DealReference);

                if (TerminalStatuses.Contains(status))
                {
                    pending.Resolved   = true;
                    pending.ResolvedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    pending.PollCount++;
                    if (pending.PollCount >= MaxPollAttempts)
                    {
                        // Never mark an unresolved payout as resolved merely because
                        // polling exhausted its retry budget. It remains part of the
                        // reconciliation reserve and can be retried by a later poll
                        // cycle or by an explicit admin action.
                        logger.LogError(
                            "PayoutPoller: max attempts reached for {PayoutId} Ref={Ref}; payout remains unresolved for reconciliation",
                            pending.PayoutId, pending.DealReference);
                        pending.PollCount = 0;
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PayoutPoller: error checking {PayoutId}", pending.PayoutId);
            }
        }
    }
}
