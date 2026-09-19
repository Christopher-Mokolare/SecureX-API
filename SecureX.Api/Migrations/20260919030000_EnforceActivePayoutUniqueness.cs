using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations;

[Migration("20260919030000_EnforceActivePayoutUniqueness")]
public partial class EnforceActivePayoutUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM pending_payouts
                    WHERE resolved = false
                    GROUP BY deal_reference
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION
                        'Cannot enforce active payout uniqueness: duplicate unresolved pending payouts exist';
                END IF;
            END
            $$;
            """);

        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ux_pending_payouts_active_deal_reference
            ON pending_payouts (deal_reference)
            WHERE resolved = false;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ux_pending_payouts_active_deal_reference;
            """);
    }
}
