using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations;

[Migration("20260919020000_CreateSystemFailureLogs")]
public partial class CreateSystemFailureLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
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

CREATE INDEX IF NOT EXISTS idx_system_failure_logs_created_at
    ON system_failure_logs (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_severity_resolved
    ON system_failure_logs (severity, resolved);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_correlation_id
    ON system_failure_logs (correlation_id);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_transaction_id
    ON system_failure_logs (transaction_id);
CREATE INDEX IF NOT EXISTS idx_system_failure_logs_fingerprint
    ON system_failure_logs (service, path, status_code, error_type, resolved, last_seen_at);
");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS system_failure_logs;");
    }
}