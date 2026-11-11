using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    api_key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    authentication_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_accounts", x => x.id);
                    table.CheckConstraint("ck_service_accounts_api_key_hash", "octet_length(api_key_hash) = 32");
                    table.CheckConstraint("ck_service_accounts_authentication_version", "authentication_version > 0");
                    table.CheckConstraint("ck_service_accounts_dates", "updated_at_utc >= created_at_utc");
                    table.CheckConstraint("ck_service_accounts_name", "name = btrim(name) AND char_length(name) BETWEEN 3 AND 64");
                    table.CheckConstraint("ck_service_accounts_normalized_name", "normalized_name = btrim(normalized_name) AND char_length(normalized_name) BETWEEN 3 AND 128");
                    table.CheckConstraint("ck_service_accounts_role", "role = 'LIS'");
                });

            migrationBuilder.CreateIndex(
                name: "ux_service_accounts_normalized_name",
                table: "service_accounts",
                column: "normalized_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_accounts");
        }
    }
}
