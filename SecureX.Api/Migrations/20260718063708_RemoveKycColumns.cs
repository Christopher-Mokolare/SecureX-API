using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveKycColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "smile_verification_status",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "smile_verification_status",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
