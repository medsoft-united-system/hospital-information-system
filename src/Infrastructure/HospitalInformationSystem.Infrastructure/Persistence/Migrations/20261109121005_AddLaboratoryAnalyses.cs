using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLaboratoryAnalyses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_record_history_type",
                table: "medical_record_history_events");

            migrationBuilder.AlterColumn<string>(
                name: "performed_by_name_snapshot",
                table: "medical_record_history_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(49)",
                oldMaxLength: 49);

            migrationBuilder.AlterColumn<Guid>(
                name: "performed_by_medical_worker_id",
                table: "medical_record_history_events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "author_kind",
                table: "medical_record_history_events",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "MedicalWorker");

            migrationBuilder.AddColumn<Guid>(
                name: "performed_by_service_account_id",
                table: "medical_record_history_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_medical_records_id_patient",
                table: "medical_records",
                columns: new[] { "id", "patient_id" });

            migrationBuilder.CreateTable(
                name: "laboratory_analysis_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    medical_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_medical_worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    laboratory_test_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loinc_code_snapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name_snapshot = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    authored_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_laboratory_analysis_requests", x => x.id);
                    table.UniqueConstraint("ak_lab_requests_id_patient", x => new { x.id, x.patient_id });
                    table.CheckConstraint("ck_lab_requests_comment", "comment IS NULL OR (comment = btrim(comment) AND char_length(comment) BETWEEN 1 AND 2000)");
                    table.CheckConstraint("ck_lab_requests_display_name", "display_name_snapshot = btrim(display_name_snapshot) AND char_length(display_name_snapshot) BETWEEN 3 AND 256");
                    table.CheckConstraint("ck_lab_requests_loinc_code", "char_length(loinc_code_snapshot) BETWEEN 3 AND 32 AND loinc_code_snapshot ~ '^[0-9]+-[0-9]$'");
                    table.CheckConstraint("ck_lab_requests_status", "status IN ('active', 'completed')");
                    table.ForeignKey(
                        name: "FK_laboratory_analysis_requests_laboratory_test_definitions_la~",
                        column: x => x.laboratory_test_definition_id,
                        principalTable: "laboratory_test_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_laboratory_analysis_requests_medical_records_medical_record~",
                        columns: x => new { x.medical_record_id, x.patient_id },
                        principalTable: "medical_records",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_laboratory_analysis_requests_medical_workers_requested_by_m~",
                        column: x => x.requested_by_medical_worker_id,
                        principalTable: "medical_workers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "laboratory_analysis_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    laboratory_analysis_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_service_account_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_laboratory_analysis_results", x => x.id);
                    table.CheckConstraint("ck_lab_results_distinct_id", "id <> laboratory_analysis_request_id");
                    table.CheckConstraint("ck_lab_results_issued_precision", "issued_at_utc = date_trunc('milliseconds', issued_at_utc)");
                    table.CheckConstraint("ck_lab_results_value", "value = btrim(value) AND char_length(value) BETWEEN 1 AND 2000");
                    table.ForeignKey(
                        name: "FK_laboratory_analysis_results_laboratory_analysis_requests_la~",
                        columns: x => new { x.laboratory_analysis_request_id, x.patient_id },
                        principalTable: "laboratory_analysis_requests",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_laboratory_analysis_results_service_accounts_created_by_ser~",
                        column: x => x.created_by_service_account_id,
                        principalTable: "service_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_medical_record_history_events_performed_by_service_account_~",
                table: "medical_record_history_events",
                column: "performed_by_service_account_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_history_author",
                table: "medical_record_history_events",
                sql: "(author_kind = 'MedicalWorker' AND performed_by_medical_worker_id IS NOT NULL AND performed_by_service_account_id IS NULL AND event_type <> 'LaboratoryAnalysisResultRecorded') OR (author_kind = 'ServiceAccount' AND performed_by_medical_worker_id IS NULL AND performed_by_service_account_id IS NOT NULL AND event_type = 'LaboratoryAnalysisResultRecorded')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_history_type",
                table: "medical_record_history_events",
                sql: "event_type IN ('MedicalRecordCreated', 'DiagnosisAdded', 'DiagnosisRemoved', 'PrescriptionAdded', 'PrescriptionCancelled', 'LaboratoryAnalysisRequested', 'LaboratoryAnalysisResultRecorded')");

            migrationBuilder.CreateIndex(
                name: "ix_lab_requests_queue",
                table: "laboratory_analysis_requests",
                columns: new[] { "status", "authored_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_laboratory_analysis_requests_laboratory_test_definition_id",
                table: "laboratory_analysis_requests",
                column: "laboratory_test_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_laboratory_analysis_requests_medical_record_id_patient_id",
                table: "laboratory_analysis_requests",
                columns: new[] { "medical_record_id", "patient_id" });

            migrationBuilder.CreateIndex(
                name: "IX_laboratory_analysis_requests_requested_by_medical_worker_id",
                table: "laboratory_analysis_requests",
                column: "requested_by_medical_worker_id");

            migrationBuilder.CreateIndex(
                name: "ix_lab_results_issued_id",
                table: "laboratory_analysis_results",
                columns: new[] { "issued_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_laboratory_analysis_results_created_by_service_account_id",
                table: "laboratory_analysis_results",
                column: "created_by_service_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_laboratory_analysis_results_laboratory_analysis_request_id_~",
                table: "laboratory_analysis_results",
                columns: new[] { "laboratory_analysis_request_id", "patient_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_lab_results_request_id",
                table: "laboratory_analysis_results",
                column: "laboratory_analysis_request_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_medical_record_history_events_service_accounts_performed_by~",
                table: "medical_record_history_events",
                column: "performed_by_service_account_id",
                principalTable: "service_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM laboratory_analysis_requests)
                       OR EXISTS (SELECT 1 FROM laboratory_analysis_results)
                       OR EXISTS (SELECT 1 FROM medical_record_history_events
                           WHERE author_kind = 'ServiceAccount'
                              OR event_type IN ('LaboratoryAnalysisRequested', 'LaboratoryAnalysisResultRecorded')) THEN
                        RAISE EXCEPTION 'Cannot downgrade laboratory analyses while medical laboratory data exists.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_medical_record_history_events_service_accounts_performed_by~",
                table: "medical_record_history_events");

            migrationBuilder.DropTable(
                name: "laboratory_analysis_results");

            migrationBuilder.DropTable(
                name: "laboratory_analysis_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_medical_records_id_patient",
                table: "medical_records");

            migrationBuilder.DropIndex(
                name: "IX_medical_record_history_events_performed_by_service_account_~",
                table: "medical_record_history_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_history_author",
                table: "medical_record_history_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_history_type",
                table: "medical_record_history_events");

            migrationBuilder.DropColumn(
                name: "author_kind",
                table: "medical_record_history_events");

            migrationBuilder.DropColumn(
                name: "performed_by_service_account_id",
                table: "medical_record_history_events");

            migrationBuilder.AlterColumn<string>(
                name: "performed_by_name_snapshot",
                table: "medical_record_history_events",
                type: "character varying(49)",
                maxLength: 49,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<Guid>(
                name: "performed_by_medical_worker_id",
                table: "medical_record_history_events",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_history_type",
                table: "medical_record_history_events",
                sql: "event_type IN ('MedicalRecordCreated', 'DiagnosisAdded', 'DiagnosisRemoved', 'PrescriptionAdded', 'PrescriptionCancelled')");
        }
    }
}
