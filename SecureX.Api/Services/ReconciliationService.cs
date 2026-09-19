using Microsoft.EntityFrameworkCore;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

public class ReconciliationService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, IConfiguration config, ILogger<ReconciliationService> logger)
    : BackgroundService
{
    private static readonly TransactionStatus[] InFlightStatuses =
    [
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
            .AsNoTracking()
            .Where(t => InFlightStatuses.Contains(t.Status))
            .ToListAsync(ct);

        var expectedFloat = inFlight.Sum(t => t.TotalCheckoutAmount);
        var provider = await FetchOzowFloatAsync(ct);

        var report = new ReconciliationReport
        {
            ExpectedFloat = expectedFloat,
            OzowFloat = provider.Balance,
            Discrepancy = provider.Balance.HasValue ? expectedFloat - provider.Balance.Value : 0m,
            AlertFired = provider.Balance.HasValue && Math.Abs(expectedFloat - provider.Balance.Value) > 0.01m,
            Status = provider.Balance.HasValue ? "Reconciled" : "ProviderUnavailable",
            Error = provider.Error,
        };

        db.ReconciliationReports.Add(report);
        await db.SaveChangesAsync(ct);

        if (report.AlertFired)
        {
            logger.LogCritical(
                "RECONCILIATION DISCREPANCY: expected R{Expected}, Ozow R{Ozow}, diff R{Diff}",
                expectedFloat, report.OzowFloat, report.Discrepancy);
        }
        else if (report.Status == "ProviderUnavailable")
        {
            logger.LogError(
                "RECONCILIATION UNAVAILABLE: expected R{Expected}. Ozow balance could not be verified: {Error}",
                expectedFloat, report.Error);
        }
        else
        {
            logger.LogInformation("Reconciliation OK. Float R{Float}", expectedFloat);
        }
    }

    private async Task<OzowFloatResult> FetchOzowFloatAsync(CancellationToken ct)
    {
        var baseUrl = config["Ozow:PayoutBaseUrl"];
        var siteCode = config["Ozow:SiteCode"];
        var apiKey = config["Ozow:PayoutApiKey"];

        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(siteCode) ||
            string.IsNullOrWhiteSpace(apiKey))
            return new(null, "Ozow payout balance configuration is incomplete.");

        var client = httpFactory.CreateClient("OzowPayout");
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl.TrimEnd('/')}/getfloatbalanceinfo");
        req.Headers.Add("SiteCode", siteCode);
        req.Headers.Add("ApiKey", apiKey);

        try
        {
            using var res = await client.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Ozow float fetch failed {Status}: {Body}", res.StatusCode, raw);
                return new(null, $"Ozow returned HTTP {(int)res.StatusCode}.");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (TryGetDecimal(doc.RootElement, "availableBalance", out var available))
                return new(available, null);
            if (TryGetDecimal(doc.RootElement, "balance", out var balance))
                return new(balance, null);

            return new(null, "Ozow response did not contain availableBalance or balance.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ozow float fetch threw");
            return new(null, "Ozow balance request failed.");
        }
    }

    private static bool TryGetDecimal(
        System.Text.Json.JsonElement root,
        string property,
        out decimal value)
    {
        value = 0m;
        if (!root.TryGetProperty(property, out var element))
            return false;

        if (element.ValueKind == System.Text.Json.JsonValueKind.Number)
            return element.TryGetDecimal(out value);

        return element.ValueKind == System.Text.Json.JsonValueKind.String &&
               decimal.TryParse(
                   element.GetString(),
                   System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }

    private sealed record OzowFloatResult(decimal? Balance, string? Error);

    private static TimeSpan TimeUntilNext0200Sast()
    {
        var sast = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
        var nowSast = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sast);
        var next = nowSast.Date.AddHours(2);
        if (nowSast >= next) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(next, sast) - DateTime.UtcNow;
    }
}