using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class ReconciliationService(IServiceScopeFactory scopeFactory, ILogger<ReconciliationService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeUntilNext0200Sast();
            logger.LogInformation("Reconciliation scheduled in {Delay}", delay);
            await Task.Delay(delay, ct);
            await RunAsync(ct);
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ozow float is the merchant-funded balance used for payouts/refunds.
        // It is NOT the same thing as SecureX customer funds currently in escrow.
        //
        // The current documented Ozow Payouts API exposes payout operations but does
        // not expose a merchant-float-balance operation. Do not infer a zero balance
        // when no authoritative provider balance is available.
        var unresolvedPayoutRefs = await db.PendingPayouts
            .AsNoTracking()
            .Where(p => !p.Resolved)
            .Select(p => p.DealReference)
            .ToListAsync(ct);

        var payoutReserve = unresolvedPayoutRefs.Count == 0
            ? 0m
            : await db.Transactions
                .AsNoTracking()
                .Where(t => t.Status == TransactionStatus.Completed &&
                            unresolvedPayoutRefs.Contains(t.DealReference))
                .SumAsync(t => t.ItemValue - t.SellerFee, ct);

        var refundReserve = await db.Transactions
            .AsNoTracking()
            .Where(t => t.Status == TransactionStatus.RequiresRefund)
            .SumAsync(t => t.TotalCheckoutAmount, ct);

        var requiredFloat = payoutReserve + refundReserve;

        // Ozow's documented Payouts API does not provide a live merchant-float
        // balance endpoint. Until Ozow supplies an approved balance/float API or
        // another authoritative integration, record the provider side as unavailable
        // rather than displaying R0.00 and creating a false shortfall.
        const string providerError =
            "Ozow does not expose a documented live float-balance endpoint in the current Payouts API. " +
            "Provider float must be verified in the Ozow merchant dashboard or via an approved Ozow balance integration.";

        var report = new ReconciliationReport
        {
            ExpectedFloat = requiredFloat,
            OzowFloat = null,
            Discrepancy = 0m,
            AlertFired = false,
            Status = "ProviderUnavailable",
            Error = providerError,
        };

        db.ReconciliationReports.Add(report);
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "RECONCILIATION PROVIDER BALANCE UNAVAILABLE: required reserve R{Expected}. {Error}",
            requiredFloat,
            providerError);
    }

    private static TimeSpan TimeUntilNext0200Sast()
    {
        var sast = FindSouthAfricaTimeZone();
        var nowSast = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sast);
        var next = nowSast.Date.AddHours(2);
        if (nowSast >= next) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(next, sast) - DateTime.UtcNow;
    }

    private static TimeZoneInfo FindSouthAfricaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time"); }
    }
}
