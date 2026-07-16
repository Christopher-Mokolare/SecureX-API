using System.Text.Json.Serialization;

namespace SecureX.Api.Models;

// ── Ozow verify ──────────────────────────────────────────────────────────────

public class PayoutVerifyResponse
{
    [JsonPropertyName("payoutId")]
    public string PayoutId { get; set; } = "";
    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; set; }
    [JsonPropertyName("accountNumberDecryptionKey")]
    public string AccountNumberDecryptionKey { get; set; } = "";
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("PayoutId")]
    public string PayoutIdPascal => PayoutId;
    [JsonPropertyName("IsVerified")]
    public bool IsVerifiedPascal => IsVerified;
    [JsonPropertyName("AccountNumberDecryptionKey")]
    public string AccountNumberDecryptionKeyPascal => AccountNumberDecryptionKey;
    [JsonPropertyName("Reason")]
    public string ReasonPascal => Reason;
}

// ── Ozow notification ────────────────────────────────────────────────────────

public class PayoutNotificationResponse
{
    public bool Received { get; set; }
    public bool Processed { get; set; }
    public bool Duplicate { get; set; }
    public bool HashValid { get; set; }
    public string? PayoutId { get; set; }
    public string? Reason { get; set; }
}

// ── Users ────────────────────────────────────────────────────────────────────

public class UserResponse
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string SmileVerificationStatus { get; set; } = "";
    public string BankVerificationStatus { get; set; } = "";
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
    public DateTime CreatedAt { get; set; }
    public DateTime? InspectionWindowEndsAt { get; set; }
    public UserResponse? Buyer { get; set; }
    public UserResponse? Seller { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = "";
}
