namespace SecureX.Api.Models;

public enum TransactionStatus
{
    PaymentPending,
    FundsSecured,
    RequiresRefund,
    LogisticsPending,
    ItemDelivered,
    Completed,
    Refunded
}

public enum ServiceType { Standard, VerifiedExpress }

public enum KycStatus { Pending, Approved, Failed }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = "";
    public string IdNumber { get; set; } = "";          // KMS-encrypted ciphertext
    public string BankAccountNumber { get; set; } = ""; // KMS-encrypted ciphertext
    public string BankBranchCode { get; set; } = "";
    public string BankGroupId { get; set; } = "";       // Ozow bank group UUID
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public KycStatus BankVerificationStatus { get; set; } = KycStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DealReference { get; set; } = "";
    public Guid BuyerId { get; set; }
    public Guid SellerId { get; set; }
    public string ItemTitle { get; set; } = "";
    public string ItemDescription { get; set; } = "";
    public string SellerLocation { get; set; } = "";
    public decimal ItemValue { get; set; }
    public decimal PlatformFee { get; set; }
    public decimal TotalCheckoutAmount { get; set; }
    public decimal BuyerFee { get; set; }               // portion buyer pays
    public decimal SellerFee { get; set; }              // portion deducted from payout
    public ServiceType ServiceType { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.PaymentPending;
    public int Version { get; set; } = 1;
    public DateTime? InspectionWindowEndsAt { get; set; } // set when ItemDelivered
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? Buyer { get; set; }
    public User? Seller { get; set; }
    public List<AuditLog> AuditLogs { get; set; } = [];
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    public TransactionStatus? PreviousStatus { get; set; }
    public TransactionStatus NewStatus { get; set; }
    public string TriggerActor { get; set; } = "";
    public string ActionDetails { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class WebhookEvent
{
    public string EventKey { get; set; } = "";          // PK — idempotency guard
    public string PayoutId { get; set; } = "";
    public int Status { get; set; }
    public int SubStatus { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PayoutNotification
{
    public long Id { get; set; }
    public string? EventKey { get; set; }
    public string PayoutId { get; set; } = "";
    public string? SiteCode { get; set; }
    public string? MerchantReference { get; set; }
    public string? CustomerMerchantReference { get; set; }
    public int? Status { get; set; }
    public int? SubStatus { get; set; }
    public bool HashValid { get; set; }
    public bool Duplicate { get; set; }
    public string? Reason { get; set; }
    public string RawPayload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ReconciliationReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public decimal ExpectedFloat { get; set; }
    public decimal OzowFloat { get; set; }
    public decimal Discrepancy { get; set; }
    public bool AlertFired { get; set; }
}

// Tracks submitted payouts so the poller can check status if no webhook arrives
public class PendingPayout
{
    public string PayoutId { get; set; } = "";          // PK — Ozow's UUID
    public string DealReference { get; set; } = "";
    public bool Resolved { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}
