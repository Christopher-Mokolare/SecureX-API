using System.Text.Json.Serialization;

namespace SecureX.Api.Models;

// ── Ozow verify ──────────────────────────────────────────────────────────────

public class PayoutVerifyResponse
{
    public string PayoutId { get; set; } = "";
    public bool IsVerified { get; set; }
    public string AccountNumberDecryptionKey { get; set; } = "";
    public string Reason { get; set; } = "";
}

// ── Ozow notification ────────────────────────────────────────────────────────

public class PayoutNotificationResponse
{
    [JsonPropertyName("received")] public bool Received { get; set; }
    [JsonPropertyName("processed")] public bool Processed { get; set; }
    [JsonPropertyName("duplicate")] public bool Duplicate { get; set; }
    [JsonPropertyName("hashValid")] public bool HashValid { get; set; }
    [JsonPropertyName("payoutId")] public string? PayoutId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

// ── Users ────────────────────────────────────────────────────────────────────

public class UserResponse
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string BankVerificationStatus { get; set; } = "";
    public string IdCheckStatus { get; set; } = "";
    public string AmlStatus { get; set; } = "";
}

// ── Transactions ─────────────────────────────────────────────────────────────

public class TransactionResponse
{
    public Guid Id { get; set; }
    public string DealReference { get; set; } = "";
    public string Status { get; set; } = "";
    public string ItemTitle { get; set; } = "";
    public string ItemDescription { get; set; } = "";
    public string SellerLocation { get; set; } = "";
    public decimal ItemValue { get; set; }
    public decimal PlatformFee { get; set; }
    public decimal BuyerFee { get; set; }
    public decimal SellerFee { get; set; }
    public decimal TotalCheckoutAmount { get; set; }
    public string ServiceType { get; set; } = "";
    public int Version { get; set; }
    public string? PaymentRedirectUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? InspectionWindowEndsAt { get; set; }
    public UserResponse? Buyer { get; set; }
    public UserResponse? Seller { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = "";
}
