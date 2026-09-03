using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class ReconciliationService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, IConfiguration config, ILogger<ReconciliationService> logger)
    : BackgroundService
{
    // In-flight statuses that hold funds in the float
    private static readonly TransactionStatus[] InFlightStatuses =
    [
        TransactionStatus.PaymentPending,
        TransactionStatus.FundsSecured,
        TransactionStatus.LogisticsPending,
        TransactionStatus.ItemDelivered,
    ];

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

        var inFlight = await db.Transactions
            .Where(t => t.Status == TransactionStatus.PaymentPending ||
                        t.Status == TransactionStatus.FundsSecured ||
                        t.Status == TransactionStatus.LogisticsPending ||
                        t.Status == TransactionStatus.ItemDelivered)
            .ToListAsync(ct);

        var expectedFloat = inFlight.Sum(t => t.ItemValue + t.PlatformFee);

        var ozowFloat = await FetchOzowFloatAsync();

        var discrepancy = expectedFloat - ozowFloat;
        var alertFired = Math.Abs(discrepancy) > 0.01m;

        db.ReconciliationReports.Add(new ReconciliationReport
        {
            ExpectedFloat = expectedFloat,
            OzowFloat = ozowFloat,
            Discrepancy = discrepancy,
            AlertFired = alertFired,
        });

        await db.SaveChangesAsync(ct);

        if (alertFired)
        {
            logger.LogCritical(
                "RECONCILIATION DISCREPANCY: expected R{Expected}, Ozow R{Ozow}, diff R{Diff}",
                expectedFloat, ozowFloat, discrepancy);
            // TODO: publish to SNS
        }
        else
        {
            logger.LogInformation("Reconciliation OK. Float R{Float}", expectedFloat);
        }
    }

    private async Task<decimal> FetchOzowFloatAsync()
    {
        var baseUrl = config["Ozow:PayoutBaseUrl"] ?? "https://stagingpayoutsapi.ozow.com/v1";
        var siteCode = config["Ozow:SiteCode"]!;
        var apiKey   = config["Ozow:PayoutApiKey"]!;

        var client = httpFactory.CreateClient("OzowPayout");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/getfloatbalanceinfo");
        req.Headers.Add("SiteCode", siteCode);
        req.Headers.Add("ApiKey", apiKey);

        try
        {
            var res = await client.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Ozow float fetch failed {Status}: {Body}", res.StatusCode, raw);
                return 0m;
            }
            var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("availableBalance", out var bal))
                return bal.GetDecimal();
            if (doc.RootElement.TryGetProperty("balance", out var b))
                return b.GetDecimal();
            return 0m;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ozow float fetch threw");
            return 0m;
        }
    }

    private static TimeSpan TimeUntilNext0200Sast()
    {
        var sast = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
        var nowSast = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sast);
        var next = nowSast.Date.AddHours(2);
        if (nowSast >= next) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(next, sast) - DateTime.UtcNow;
    }
}
