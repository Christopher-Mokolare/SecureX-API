using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SecureX.Api.Data;

namespace SecureX.Api.Services;

public sealed record SystemFailureLogRecord(
    Guid Id,
    string Severity,
    string Category,
    string Service,
    string Environment,
    string Method,
    string Path,
    int StatusCode,
    string? ErrorType,
    string Message,
    string? Exception,
    string? StackTrace,
    string? CorrelationId,
    string? UserId,
    string? Provider,
    Guid? TransactionId,
    int OccurrenceCount,
    bool Resolved,
    DateTime? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionNotes,
    DateTime CreatedAt,
    DateTime LastSeenAt);

public sealed record PagedSystemFailureLogs(
    IReadOnlyList<SystemFailureLogRecord> Items,
    int Total,
    int Page,
    int Size,
    int Pages);

public sealed class SystemFailureLogService(AppDbContext db)
{
    private const string CreateTableSql = @"
CREATE TABLE IF NOT EXISTS system_failure_logs (
    id uuid PRIMARY KEY,
    severity text NOT NULL,
    category text NOT NULL,
    service text NOT NULL,
    environment text NOT NULL,
    method text NOT NULL,
    path text NOT NULL,
    status_code integer NOT NULL,
    error_type text NULL,
    message text NOT NULL,
    exception text NULL,
    stack_trace text NULL,
    correlation_id text NULL,
    user_id text NULL,
    provider text NULL,
    transaction_id uuid NULL,
    occurrence_count integer NOT NULL DEFAULT 1,
    resolved boolean NOT NULL DEFAULT false,
    resolved_at timestamptz NULL,
    resolved_by text NULL,
    resolution_notes text NULL,
    created_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_created_at ON system_failure_logs (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_severity_resolved ON system_failure_logs (severity, resolved);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_correlation_id ON system_failure_logs (correlation_id);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_transaction_id ON system_failure_logs (transaction_id);
";

    private bool schemaReady;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (schemaReady) return;
        await db.Database.ExecuteSqlRawAsync(CreateTableSql, cancellationToken);
        schemaReady = true;
    }

    public async Task RecordAsync(
        string severity,
        string category,
        string service,
        string environment,
        string method,
        string path,
        int statusCode,
        string message,
        string? errorType = null,
        string? exception = null,
        string? stackTrace = null,
        string? correlationId = null,
        string? userId = null,
        string? provider = null,
        Guid? transactionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureSchemaAsync(cancellationToken);

            const string sql = @"
INSERT INTO system_failure_logs
(id, severity, category, service, environment, method, path, status_code, error_type, message,
 exception, stack_trace, correlation_id, user_id, provider, transaction_id, occurrence_count,
 resolved, created_at, last_seen_at)
VALUES
(@id, @severity, @category, @service, @environment, @method, @path, @status_code, @error_type, @message,
 @exception, @stack_trace, @correlation_id, @user_id, @provider, @transaction_id, 1,
 false, @created_at, @last_seen_at);";

            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlRawAsync(sql,
                Parameters(
                    new NpgsqlParameter("id", Guid.NewGuid()),
                    new NpgsqlParameter("severity", severity),
                    new NpgsqlParameter("category", category),
                    new NpgsqlParameter("service", service),
                    new NpgsqlParameter("environment", environment),
                    new NpgsqlParameter("method", method),
                    new NpgsqlParameter("path", path),
                    new NpgsqlParameter("status_code", statusCode),
                    NullableParameter("error_type", errorType),
                    new NpgsqlParameter("message", message),
                    NullableParameter("exception", exception),
                    NullableParameter("stack_trace", stackTrace),
                    NullableParameter("correlation_id", correlationId),
                    NullableParameter("user_id", userId),
                    NullableParameter("provider", provider),
                    NullableParameter("transaction_id", transactionId),
                    new NpgsqlParameter("created_at", now),
                    new NpgsqlParameter("last_seen_at", now)),
                cancellationToken);
        }
        catch
        {
            // Failure logging must never break the original API request.
        }
    }

    public async Task<PagedSystemFailureLogs> GetAsync(
        int page,
        int size,
        string? search,
        string? severity,
        bool? resolved,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 100);

        var where = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(message ILIKE @search OR path ILIKE @search OR correlation_id ILIKE @search OR provider ILIKE @search OR error_type ILIKE @search)");
            parameters.Add(new NpgsqlParameter("search", $"%{search}%"));
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            where.Add("severity = @severity");
            parameters.Add(new NpgsqlParameter("severity", severity));
        }

        if (resolved.HasValue)
        {
            where.Add("resolved = @resolved");
            parameters.Add(new NpgsqlParameter("resolved", resolved.Value));
        }

        var whereSql = where.Count == 0 ? "" : $"WHERE {string.Join(" AND ", where)}";
        var countSql = $"SELECT COUNT(*) FROM system_failure_logs {whereSql}";

        await using var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = countSql;
        foreach (var parameter in parameters)
            countCommand.Parameters.Add(Clone(parameter));
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var offset = (page - 1) * size;
        var listSql = $@"
SELECT id, severity, category, service, environment, method, path, status_code,
       error_type, message, exception, stack_trace, correlation_id, user_id, provider,
       transaction_id, occurrence_count, resolved, resolved_at, resolved_by, resolution_notes,
       created_at, last_seen_at
FROM system_failure_logs
{whereSql}
ORDER BY created_at DESC
OFFSET @offset LIMIT @limit;";

        await using var listCommand = connection.CreateCommand();
        listCommand.CommandText = listSql;
        foreach (var parameter in parameters)
            listCommand.Parameters.Add(Clone(parameter));
        listCommand.Parameters.Add(new NpgsqlParameter("offset", offset));
        listCommand.Parameters.Add(new NpgsqlParameter("limit", size));

        var items = new List<SystemFailureLogRecord>();
        await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(ReadRecord(reader));

        return new PagedSystemFailureLogs(
            items,
            total,
            page,
            size,
            (int)Math.Ceiling(total / (double)size));
    }

    public async Task<bool> SetResolvedAsync(Guid id, bool resolved, string actor, string? notes, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = @"
UPDATE system_failure_logs
SET resolved = @resolved,
    resolved_at = CASE WHEN @resolved THEN CURRENT_TIMESTAMP ELSE NULL END,
    resolved_by = CASE WHEN @resolved THEN @actor ELSE NULL END,
    resolution_notes = @notes,
    last_seen_at = CURRENT_TIMESTAMP
WHERE id = @id;";

        var affected = await db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("resolved", resolved),
            new NpgsqlParameter("actor", actor),
            NullableParameter("notes", notes),
            new NpgsqlParameter("id", id),
            cancellationToken);

        return affected > 0;
    }

    private static SystemFailureLogRecord ReadRecord(System.Data.Common.DbDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt32(7), NullableString(r, 8), r.GetString(9),
        NullableString(r, 10), NullableString(r, 11), NullableString(r, 12), NullableString(r, 13),
        NullableString(r, 14), NullableGuid(r, 15), r.GetInt32(16), r.GetBoolean(17),
        NullableDate(r, 18), NullableString(r, 19), NullableString(r, 20), r.GetDateTime(21), r.GetDateTime(22));

    private static string? NullableString(System.Data.Common.DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    private static Guid? NullableGuid(System.Data.Common.DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetGuid(ordinal);
    private static DateTime? NullableDate(System.Data.Common.DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetDateTime(ordinal);

    private static NpgsqlParameter NullableParameter(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private static NpgsqlParameter Clone(NpgsqlParameter source) => new(source.ParameterName, source.Value ?? DBNull.Value);
    private static NpgsqlParameter[] Parameters(params NpgsqlParameter[] parameters) => parameters;
}
