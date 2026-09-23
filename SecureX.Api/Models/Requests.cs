using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace SecureX.Api.Models;

// ── Ozow collection payment notification (form POST from Ozow) ──────────────

public class OzowPaymentNotification
{
    public string SiteCode { get; set; } = "";
    public string TransactionReference { get; set; } = "";
    public string BankReference { get; set; } = "";
    public string? Optional1 { get; set; }
    public string Amount { get; set; } = "";
    public string Status { get; set; } = "";
    public string HashCheck { get; set; } = "";
}

// ── Ozow verify webhook ──────────────────────────────────────────────────────

public class BankingDetails
{
    [JsonPropertyName("bankGroupId")] public string BankGroupId { get; set; } = "";
    [JsonPropertyName("accountNumber")] public string AccountNumber { get; set; } = "";
    [JsonPropertyName("branchCode")] public string BranchCode { get; set; } = "";
}

public class PayoutVerifyRequest
{
    [JsonPropertyName("payoutId")] public string PayoutId { get; set; } = "";
    [JsonPropertyName("siteCode")] public string SiteCode { get; set; } = "";
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
    [JsonPropertyName("merchantReference")] public string MerchantReference { get; set; } = "";
    [JsonPropertyName("customerBankReference")] public string CustomerBankReference { get; set; } = "";
    [JsonPropertyName("isRtc")] public bool IsRtc { get; set; }
    [JsonPropertyName("notifyUrl")] public string NotifyUrl { get; set; } = "";
    [JsonPropertyName("verifyUrl")] public string VerifyUrl { get; set; } = "";
    [JsonPropertyName("bankingDetails")] public BankingDetails? BankingDetails { get; set; }
    [JsonPropertyName("hashCheck")] public string HashCheck { get; set; } = "";
}

// ── Ozow notification webhook ────────────────────────────────────────────────

public class PayoutStatusNested
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("subStatus")] public int SubStatus { get; set; }
}

public class PayoutNotificationRequest
{
    [JsonPropertyName("payoutId")] public string PayoutId { get; set; } = "";
    [JsonPropertyName("siteCode")] public string SiteCode { get; set; } = "";
    [JsonPropertyName("merchantReference")] public string MerchantReference { get; set; } = "";
    [JsonPropertyName("customerMerchantReference")] public string CustomerMerchantReference { get; set; } = "";
    [JsonPropertyName("payoutStatus")] public object? PayoutStatus { get; set; }
    [JsonPropertyName("payoutSubStatus")] public int? PayoutSubStatus { get; set; }
    [JsonPropertyName("subStatus")] public int? SubStatus { get; set; }
    [JsonPropertyName("hashCheck")] public string HashCheck { get; set; } = "";
}

// ── User endpoints ───────────────────────────────────────────────────────────

public class RegisterUserRequest
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
}

// ── Transaction endpoints ────────────────────────────────────────────────────

public enum FeePayer { Buyer, Seller, Split }

public class CreateTransactionRequest
{
    public string BuyerFullName { get; set; } = "";
    public string BuyerEmail { get; set; } = "";
    public string BuyerPhone { get; set; } = "";
    public string BuyerIdNumber { get; set; } = "";
    public string SellerFullName { get; set; } = "";
    public string SellerEmail { get; set; } = "";
    public string SellerPhone { get; set; } = "";
    public string ItemTitle { get; set; } = "";
    public string ItemDescription { get; set; } = "";
    
    public decimal ItemValue { get; set; }
    
    public string SellerLocation { get; set; } = "";
    public ServiceType ServiceType { get; set; }
    public FeePayer FeePayer { get; set; } = FeePayer.Buyer;
}

public class RejectItemRequest
{
    public string Reason { get; set; } = "";
}

public class BankDetailsRequest
{
    public string AccountNumber { get; set; } = "";
    public string BranchCode { get; set; } = "";
    public string BankGroupId { get; set; } = "";
    public string IdNumber { get; set; } = "";
}

public class ResolveDisputeRequest
{
    public string Decision { get; set; } = ""; // "release-to-seller" | "refund-to-buyer"
}

public class AdvanceStateRequest
{
    public Guid TransactionId { get; set; }
    public int ExpectedVersion { get; set; }
}