using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureX.Api.Migrations
{
    public partial class AddAdminPassword : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "users",
                type: "text",
                nullable: true);

            // Upsert admin user: info@secureexchange.co.za  password: 664*Mogokg
            var hash = BCrypt.Net.BCrypt.HashPassword("664*Mogokg");
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM users WHERE email = 'info@secureexchange.co.za') THEN
                    UPDATE users
                    SET "IsAdmin" = true, password_hash = '{hash}', updated_at = now()
                    WHERE email = 'info@secureexchange.co.za';
                  ELSE
                    INSERT INTO users (
                        "Id", full_name, email, phone,
                        id_number, bank_account_number, bank_branch_code, bank_group_id,
                        "IdCheckStatus", "AmlStatus", liveness_status, bank_verification_status,
                        "IsAdmin", "IsSuspended", password_hash,
                        created_at, updated_at
                    ) VALUES (
                        gen_random_uuid(), 'SecureX Admin', 'info@secureexchange.co.za', '',
                        '', '', '', '',
                        'Pending', 'Pending', 'Pending', 'Pending',
                        true, false, '{hash}',
                        now(), now()
                    );
                  END IF;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "password_hash", table: "users");
        }
    }
}
