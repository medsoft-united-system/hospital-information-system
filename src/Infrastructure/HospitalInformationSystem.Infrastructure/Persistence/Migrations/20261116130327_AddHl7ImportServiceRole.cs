using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddHl7ImportServiceRole : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_service_accounts_role",
            table: "service_accounts");

        migrationBuilder.AddCheckConstraint(
            name: "ck_service_accounts_role",
            table: "service_accounts",
            sql: "role IN ('LIS', 'HL7Import')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_service_accounts_role",
            table: "service_accounts");

        migrationBuilder.AddCheckConstraint(
            name: "ck_service_accounts_role",
            table: "service_accounts",
            sql: "role = 'LIS'");
    }
}
