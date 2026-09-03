using Microsoft.EntityFrameworkCore;
using SecureX.Api.Models;

namespace SecureX.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<PayoutNotification> PayoutNotifications => Set<PayoutNotification>();
    public DbSet<ReconciliationReport> ReconciliationReports => Set<ReconciliationReport>();
    public DbSet<PendingPayout> PendingPayouts => Set<PendingPayout>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.FullName).HasColumnName("full_name");
            e.Property(x => x.IdNumber).HasColumnName("id_number");
            e.Property(x => x.BankAccountNumber).HasColumnName("bank_account_number");
            e.Property(x => x.BankBranchCode).HasColumnName("bank_branch_code");
             e.Property(x => x.BankGroupId).HasColumnName("bank_group_id");
            e.Property(x => x.SmileIdJobId).HasColumnName("smile_id_job_id");
            e.Property(x => x.SmileIdAmlJobId).HasColumnName("smile_id_aml_job_id");
            e.Property(x => x.BankVerificationStatus)
                .HasColumnName("bank_verification_status")
                .HasConversion<string>();
            e.Property(x => x.IdCheckStatus).HasConversion<string>();
            e.Property(x => x.AmlStatus).HasConversion<string>();
            e.Property(x => x.LivenessStatus)
                .HasColumnName("liveness_status")
                .HasConversion<string>();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<Transaction>(e =>
        {
            e.ToTable("transactions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DealReference).IsUnique();
            e.Property(x => x.DealReference).HasColumnName("deal_reference");
            e.Property(x => x.BuyerId).HasColumnName("buyer_id");
            e.Property(x => x.SellerId).HasColumnName("seller_id");
            e.Property(x => x.ItemTitle).HasColumnName("item_title");
            e.Property(x => x.ItemDescription).HasColumnName("item_description");
            e.Property(x => x.SellerLocation).HasColumnName("seller_location");
            e.Property(x => x.ItemValue).HasColumnName("item_value").HasColumnType("numeric(12,2)");
            e.Property(x => x.PlatformFee).HasColumnName("platform_fee").HasColumnType("numeric(12,2)");
            e.Property(x => x.BuyerFee).HasColumnName("buyer_fee").HasColumnType("numeric(12,2)");
            e.Property(x => x.SellerFee).HasColumnName("seller_fee").HasColumnType("numeric(12,2)");
            e.Property(x => x.TotalCheckoutAmount).HasColumnName("total_checkout_amount").HasColumnType("numeric(12,2)");
            e.Property(x => x.ServiceType).HasColumnName("service_type").HasConversion<string>();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.InspectionWindowEndsAt).HasColumnName("inspection_window_ends_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne(x => x.Buyer).WithMany().HasForeignKey(x => x.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Seller).WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.AuditLogs).WithOne().HasForeignKey(x => x.TransactionId).IsRequired(false);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.TransactionId).HasColumnName("transaction_id");
            e.Property(x => x.PreviousStatus).HasColumnName("previous_status").HasConversion<string?>();
            e.Property(x => x.NewStatus).HasColumnName("new_status").HasConversion<string>();
            e.Property(x => x.TriggerActor).HasColumnName("trigger_actor");
            e.Property(x => x.ActionDetails).HasColumnName("action_details");
            e.Property(x => x.Timestamp).HasColumnName("timestamp");
        });

        b.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            e.HasKey(x => x.EventKey);
            e.Property(x => x.EventKey).HasColumnName("event_key");
            e.Property(x => x.PayoutId).HasColumnName("payout_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<PayoutNotification>(e =>
        {
            e.ToTable("payout_notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventKey).HasColumnName("event_key");
            e.Property(x => x.PayoutId).HasColumnName("payout_id");
            e.Property(x => x.SiteCode).HasColumnName("site_code");
            e.Property(x => x.MerchantReference).HasColumnName("merchant_reference");
            e.Property(x => x.CustomerMerchantReference).HasColumnName("customer_merchant_reference");
            e.Property(x => x.HashValid).HasColumnName("hash_valid");
            e.Property(x => x.RawPayload).HasColumnName("raw_payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.PayoutId).HasDatabaseName("idx_payout_notifications_payout_id");
        });

        b.Entity<ReconciliationReport>(e =>
        {
            e.ToTable("reconciliation_reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.RunAt).HasColumnName("run_at");
            e.Property(x => x.ExpectedFloat).HasColumnName("expected_float").HasColumnType("numeric(14,2)");
            e.Property(x => x.OzowFloat).HasColumnName("ozow_float").HasColumnType("numeric(14,2)");
            e.Property(x => x.Discrepancy).HasColumnName("discrepancy").HasColumnType("numeric(14,2)");
            e.Property(x => x.AlertFired).HasColumnName("alert_fired");
        });

        b.Entity<PendingPayout>(e =>
        {
            e.ToTable("pending_payouts");
            e.HasKey(x => x.PayoutId);
            e.Property(x => x.PayoutId).HasColumnName("payout_id");
            e.Property(x => x.DealReference).HasColumnName("deal_reference");
            e.Property(x => x.Resolved).HasColumnName("resolved");
            e.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.HasIndex(x => x.Resolved).HasDatabaseName("idx_pending_payouts_resolved");
        });
    }
}
