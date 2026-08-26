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
            // Columns may already exist with old name (existing DB) or not at all (fresh DB)
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='id_check_status') THEN
                        ALTER TABLE users RENAME COLUMN id_check_status TO ""IdCheckStatus"";
                        ALTER TABLE users ALTER COLUMN ""IdCheckStatus"" TYPE text USING ""IdCheckStatus""::text;
                    ELSIF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='IdCheckStatus') THEN
                        ALTER TABLE users ADD COLUMN ""IdCheckStatus"" text NOT NULL DEFAULT '';
                    END IF;

                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='aml_status') THEN
                        ALTER TABLE users RENAME COLUMN aml_status TO ""AmlStatus"";
                        ALTER TABLE users ALTER COLUMN ""AmlStatus"" TYPE text USING ""AmlStatus""::text;
                    ELSIF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='AmlStatus') THEN
                        ALTER TABLE users ADD COLUMN ""AmlStatus"" text NOT NULL DEFAULT '';
                    END IF;
                END;
                $$;
            ");
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
