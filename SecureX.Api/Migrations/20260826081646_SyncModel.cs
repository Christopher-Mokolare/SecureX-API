using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations
{
    /// <inheritdoc />
    public partial class SyncModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "id_check_status",
                table: "users",
                newName: "IdCheckStatus");

            migrationBuilder.RenameColumn(
                name: "aml_status",
                table: "users",
                newName: "AmlStatus");

            migrationBuilder.AlterColumn<string>(
                name: "IdCheckStatus",
                table: "users",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "AmlStatus",
                table: "users",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IdCheckStatus",
                table: "users",
                newName: "id_check_status");

            migrationBuilder.RenameColumn(
                name: "AmlStatus",
                table: "users",
                newName: "aml_status");

            migrationBuilder.AlterColumn<int>(
                name: "id_check_status",
                table: "users",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<int>(
                name: "aml_status",
                table: "users",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
