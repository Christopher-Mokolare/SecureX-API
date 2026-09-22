namespace SecureX.Api.Models;

public sealed class TransactionLimitsOptions
{
    public decimal MinimumAmount { get; set; } = 500m;
    public decimal MaximumAmount { get; set; } = 30000m;
}
