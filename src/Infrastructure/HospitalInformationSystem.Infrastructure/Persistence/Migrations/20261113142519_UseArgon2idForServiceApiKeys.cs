using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations;

public partial class UseArgon2idForServiceApiKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_service_accounts_api_key_hash",
            table: "service_accounts");

        migrationBuilder.AddCheckConstraint(
            name: "ck_service_accounts_api_key_hash",
            table: "service_accounts",
            sql: "octet_length(api_key_hash) = 32 OR (octet_length(api_key_hash) BETWEEN 90 AND 128 AND substring(api_key_hash FROM 1 FOR 15) = convert_to('$argon2id$v=19$', 'UTF8'))");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE service_accounts IN ACCESS EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM service_accounts WHERE octet_length(api_key_hash) <> 32) THEN
                    RAISE EXCEPTION 'Откат невозможен: сервисные аккаунты содержат хеши Argon2id.';
                END IF;
            END $$;
            """);

        migrationBuilder.DropCheckConstraint(
            name: "ck_service_accounts_api_key_hash",
            table: "service_accounts");

        migrationBuilder.AddCheckConstraint(
            name: "ck_service_accounts_api_key_hash",
            table: "service_accounts",
            sql: "octet_length(api_key_hash) = 32");
    }
}
