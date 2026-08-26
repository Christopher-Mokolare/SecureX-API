using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSmileIdJobIdFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='smile_id_job_id') THEN
                        ALTER TABLE users ADD COLUMN smile_id_job_id text NULL;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
