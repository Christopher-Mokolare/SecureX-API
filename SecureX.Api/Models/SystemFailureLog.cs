namespace SecureX.Api.Models;

public enum SystemFailureSeverity
{
    Warning,
    Error,
    Critical
}

public enum SystemFailureStatus
{
    Open,
    Investigating,
    Resolved,
    Closed
}

public class SystemFailureLog
{
    public long Id { get; set; }
    public SystemFailureSeverity Severity { get; set; } = SystemFailureSeverity.Error;
    public SystemFailureStatus Status { get; set; } = SystemFailureStatus.Open;
    public string Service { get; set; } = "API";
    public string Environment { get; set; } = "Production";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int HttpStatus { get; set; }
    public string? ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public string? TransactionId { get; set; }
    public string? UserId { get; set; }
    public string? ActorEmail { get; set; }
    public string? Provider { get; set; }
    public string? ProviderReference { get; set; }
    public string CorrelationId { get; set; } = "";
    public string? IpAddress { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public string? AssignedTo { get; set; }
    public string? ResolutionNotes { get; set; }
}
