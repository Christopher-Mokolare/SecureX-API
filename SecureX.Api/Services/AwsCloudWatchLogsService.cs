using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;

namespace SecureX.Api.Services;

public sealed record AwsLogEntry(
    string Timestamp,
    string Message,
    string? LogStreamName,
    string? EventId);

public sealed record AwsLogPage(
    IReadOnlyList<AwsLogEntry> Items,
    string? NextToken,
    string LogGroup,
    DateTime From,
    DateTime To);

public sealed class AwsCloudWatchLogsService(IAmazonCloudWatchLogs logs, IConfiguration config)
{
    private const int MaxLimit = 100;

    private string LogGroup =>
        config["AWS_CLOUDWATCH_LOG_GROUP"]
        ?? config["CloudWatch:LogGroup"]
        ?? "/ecs/securex-api";

    public async Task<AwsLogPage> GetAsync(
        DateTime from,
        DateTime to,
        string? search,
        string? level,
        int limit,
        string? nextToken,
        CancellationToken cancellationToken = default)
    {
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (to <= from)
            to = from.AddHours(1);

        limit = Math.Clamp(limit, 1, MaxLimit);

        var filter = BuildFilter(search, level);
        var request = new FilterLogEventsRequest
        {
            LogGroupName = LogGroup,
            StartTime = new DateTimeOffset(from).ToUnixTimeMilliseconds(),
            EndTime = new DateTimeOffset(to).ToUnixTimeMilliseconds(),
            Limit = limit,
            FilterPattern = filter,
            NextToken = string.IsNullOrWhiteSpace(nextToken) ? null : nextToken,
        };

        try
        {
            var response = await logs.FilterLogEventsAsync(request, cancellationToken);

            return new AwsLogPage(
                response.Events.Select(x => new AwsLogEntry(
                    DateTimeOffset.FromUnixTimeMilliseconds(x.Timestamp).UtcDateTime.ToString("O"),
                    x.Message,
                    x.LogStreamName,
                    x.EventId)).ToList(),
                response.NextToken,
                LogGroup,
                from,
                to);
        }
        catch (ResourceNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"CloudWatch log group '{LogGroup}' was not found in AWS_REGION '{config["AWS_REGION"] ?? "configured region"}'.",
                ex);
        }
    }

    private static string? BuildFilter(string? search, string? level)
    {
        var terms = new List<string>();

        if (!string.IsNullOrWhiteSpace(level))
            terms.Add(level.Trim());

        if (!string.IsNullOrWhiteSpace(search))
            terms.Add(search.Trim());

        return terms.Count == 0 ? null : string.Join(" ", terms.Select(EscapeFilterTerm));
    }

    private static string EscapeFilterTerm(string value)
    {
        var sanitized = value.Replace("\"", string.Empty).Replace("\r", " ").Replace("\n", " ");
        return sanitized.Contains(' ') ? $"\"{sanitized}\"" : sanitized;
    }
}
