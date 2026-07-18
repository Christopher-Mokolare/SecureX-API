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
    public string BankGroupId { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string BranchCode { get; set; } = "";
}

public class PayoutVerifyRequest
{
    public string PayoutId { get; set; } = "";
    public string SiteCode { get; set; } = "";
    public decimal Amount { get; set; }
    public string MerchantReference { get; set; } = "";
    public string CustomerBankReference { get; set; } = "";
    public bool IsRtc { get; set; }
    public string NotifyUrl { get; set; } = "";
    public BankingDetails? BankingDetails { get; set; }
    public string HashCheck { get; set; } = "";
}

// ── Ozow notification webhook ────────────────────────────────────────────────

public class PayoutStatusNested
{
    public int Status { get; set; }
    public int SubStatus { get; set; }
}

public class PayoutNotificationRequest
{
    public string PayoutId { get; set; } = "";
    public string SiteCode { get; set; } = "";
    public string MerchantReference { get; set; } = "";
    public string CustomerMerchantReference { get; set; } = "";

    // Ozow sends either flat ints or a nested object
    public object? PayoutStatus { get; set; }
    public int? PayoutSubStatus { get; set; }
    public int? SubStatus { get; set; }

    public string HashCheck { get; set; } = "";
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
    public string Actor { get; set; } = "admin";
}

public class AdvanceStateRequest
{
    public Guid TransactionId { get; set; }
    public string Actor { get; set; } = "";
    public string? Details { get; set; }
    public int ExpectedVersion { get; set; }
}

