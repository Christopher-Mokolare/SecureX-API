using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

/// <summary>
/// Runs every 5 minutes and auto-accepts any ItemDelivered transaction whose
/// 24-hour inspection window has expired without buyer action, then triggers payout.
/// </summary>
public class InspectionWindowExpiryService(IServiceScopeFactory scopeFactory,
    ILogger<InspectionWindowExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await ProcessExpiredAsync(ct); }
            catch (Exception ex) { logger.LogError(ex, "InspectionExpiry: unhandled error"); }

            await Task.Delay(Interval, ct);
        }
    }

    private async Task ProcessExpiredAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db        = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var txService = scope.ServiceProvider.GetRequiredService<TransactionService>();

        var expired = await db.Transactions
            .Where(t => t.Status == TransactionStatus.ItemDelivered
                     && t.InspectionWindowEndsAt.HasValue
                     && t.InspectionWindowEndsAt.Value <= DateTime.UtcNow)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        logger.LogInformation("InspectionExpiry: {Count} transaction(s) expired", expired.Count);

        foreach (var tx in expired)
        {
            try
            {
                await txService.CompleteAsync(tx.Id, "system-expiry", tx.Version);
                logger.LogInformation("InspectionExpiry: auto-accepted {Ref}", tx.DealReference);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogWarning("InspectionExpiry: concurrency conflict on {Ref} — will retry next cycle", tx.DealReference);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "InspectionExpiry: failed to auto-accept {Ref}", tx.DealReference);
            }
        }
    }
}
