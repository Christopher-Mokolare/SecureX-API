using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSmileIdAmlJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "smile_id_aml_job_id",
                table: "users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "smile_id_aml_job_id",
                table: "users");
        }
    }
}
