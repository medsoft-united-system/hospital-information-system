using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations
{
    public partial class IndexFhirServiceRequestPages : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lab_requests_queue",
                table: "laboratory_analysis_requests");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "authored_at_fhir_utc",
                table: "laboratory_analysis_requests",
                type: "timestamp with time zone",
                nullable: false,
                computedColumnSql: "date_trunc('milliseconds', authored_at_utc AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_lab_requests_queue",
                table: "laboratory_analysis_requests",
                columns: new[] { "status", "authored_at_fhir_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_lab_requests_fhir_order",
                table: "laboratory_analysis_requests",
                columns: new[] { "authored_at_fhir_utc", "id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lab_requests_queue",
                table: "laboratory_analysis_requests");

            migrationBuilder.DropIndex(
                name: "ix_lab_requests_fhir_order",
                table: "laboratory_analysis_requests");

            migrationBuilder.DropColumn(
                name: "authored_at_fhir_utc",
                table: "laboratory_analysis_requests");

            migrationBuilder.CreateIndex(
                name: "ix_lab_requests_queue",
                table: "laboratory_analysis_requests",
                columns: new[] { "status", "authored_at_utc", "id" });
        }
    }
}
