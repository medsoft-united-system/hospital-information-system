using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddLaboratoryTests : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "laboratory_test_definitions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                loinc_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_laboratory_test_definitions", x => x.id);
                table.CheckConstraint("ck_laboratory_tests_dates", "updated_at_utc >= created_at_utc");
                table.CheckConstraint("ck_laboratory_tests_display_name", "display_name = btrim(display_name) AND char_length(display_name) BETWEEN 3 AND 256");
                table.CheckConstraint("ck_laboratory_tests_loinc_code", "char_length(loinc_code) BETWEEN 3 AND 32 AND loinc_code ~ '^[0-9]+-[0-9]$'");
            });

        migrationBuilder.CreateIndex(
            name: "ux_laboratory_tests_loinc_code",
            table: "laboratory_test_definitions",
            column: "loinc_code",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "laboratory_test_definitions");
    }
}
