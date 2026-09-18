using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;

namespace SecureX.Api.Services;

public sealed record AwsLogEntry(
    string Timestamp,
    string Level,
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
                response.Events.Select(x =>
                {
                    var message = x.Message ?? string.Empty;
                    return new AwsLogEntry(
                        DateTimeOffset.FromUnixTimeMilliseconds(x.Timestamp).UtcDateTime.ToString("O"),
                        DetectLevel(message),
                        message,
                        x.LogStreamName,
                        x.EventId);
                }).ToList(),
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
            terms.Add(BuildLevelPattern(level));

        if (!string.IsNullOrWhiteSpace(search))
            terms.Add(EscapeFilterTerm(search.Trim()));

        return terms.Count == 0 ? null : string.Join(" ", terms);
    }

    private static string BuildLevelPattern(string level)
    {
        var normalized = level.Trim().ToUpperInvariant();
        var values = normalized switch
        {
            "ERROR" => new[] { "ERROR", "Error", "error", "FAIL", "Fail", "fail", "FATAL", "Fatal", "fatal", "EXCEPTION", "Exception", "exception" },
            "WARN" => new[] { "WARN", "Warn", "warn", "WARNING", "Warning", "warning" },
            "INFO" => new[] { "INFO", "Info", "info", "INFORMATION", "Information", "information" },
            _ => throw new ArgumentException($"Unsupported log level '{level}'.", nameof(level))
        };

        return "%" + string.Join("|", values.Select(RegexEscape)) + "%";
    }

    private static string RegexEscape(string value) =>
        value.Replace("\", "\\").Replace(".", "\.").Replace("*", "\*")
             .Replace("?", "\?").Replace("+", "\+").Replace("{", "\{")
             .Replace("}", "\}").Replace("[", "\[").Replace("]", "\]")
             .Replace("(", "\(").Replace(")", "\)").Replace("^", "\^")
             .Replace("$", "\$").Replace("|", "\|");

    private static string EscapeFilterTerm(string value)
    {
        var sanitized = value
            .Replace(""", string.Empty)
            .Replace("", " ")
            .Replace("
", " ");

        return sanitized.Contains(' ')
            ? """ + sanitized + """
            : sanitized;
    }

    private static string DetectLevel(string message)
    {
        var value = message.ToUpperInvariant();

        if (value.Contains("CRITICAL") || value.Contains("FATAL") ||
            value.Contains("ERROR") || value.Contains("EXCEPTION") ||
            value.Contains("FAIL:") || value.Contains("FAILURE"))
            return "ERROR";

        if (value.Contains("WARN") || value.Contains("WARNING"))
            return "WARN";

        if (value.Contains("INFO") || value.Contains("INFORMATION"))
            return "INFO";

        return "LOG";
    }
}
