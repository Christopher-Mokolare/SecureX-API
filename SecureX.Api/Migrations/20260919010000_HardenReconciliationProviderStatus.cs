using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations;

[Migration("20260919010000_HardenReconciliationProviderStatus")]
public partial class HardenReconciliationProviderStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<decimal>(
            name: "ozow_float",
            table: "reconciliation_reports",
            type: "numeric(14,2)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(14,2)");

        migrationBuilder.AddColumn<string>(
            name: "status",
            table: "reconciliation_reports",
            type: "text",
            nullable: false,
            defaultValue: "Legacy");

        migrationBuilder.AddColumn<string>(
            name: "error",
            table: "reconciliation_reports",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "status", table: "reconciliation_reports");
        migrationBuilder.DropColumn(name: "error", table: "reconciliation_reports");

        migrationBuilder.AlterColumn<decimal>(
            name: "ozow_float",
            table: "reconciliation_reports",
            type: "numeric(14,2)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(14,2)",
            oldNullable: true);
    }
}