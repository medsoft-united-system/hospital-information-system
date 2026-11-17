using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAtomicHl7PatientImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_record_history_author",
                table: "medical_record_history_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_code",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_name",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_diagnosis_definitions_code",
                table: "diagnosis_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_diagnosis_definitions_name",
                table: "diagnosis_definitions");

            migrationBuilder.AlterColumn<string>(
                name: "diagnosis_name_snapshot",
                table: "medical_record_diagnoses",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24);

            migrationBuilder.AlterColumn<string>(
                name: "diagnosis_code_snapshot",
                table: "medical_record_diagnoses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(6)",
                oldMaxLength: 6);

            migrationBuilder.AlterColumn<Guid>(
                name: "added_by_medical_worker_id",
                table: "medical_record_diagnoses",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "added_by_service_account_id",
                table: "medical_record_diagnoses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coding_system",
                table: "medical_record_diagnoses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "diagnosed_at_offset_minutes",
                table: "medical_record_diagnoses",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "diagnosed_at_utc",
                table: "medical_record_diagnoses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_type",
                table: "medical_record_diagnoses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "diagnosis_definitions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24);

            migrationBuilder.AlterColumn<string>(
                name: "code",
                table: "diagnosis_definitions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(6)",
                oldMaxLength: 6);

            migrationBuilder.CreateTable(
                name: "patient_external_identifiers",
                columns: table => new
                {
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_patient_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_external_identifiers", x => new { x.source_system, x.external_patient_id });
                    table.CheckConstraint("ck_patient_external_identifiers_external_id", "char_length(external_patient_id) BETWEEN 1 AND 128 AND external_patient_id = btrim(external_patient_id)");
                    table.CheckConstraint("ck_patient_external_identifiers_source", "char_length(source_system) BETWEEN 1 AND 64 AND source_system = btrim(source_system)");
                    table.ForeignKey(
                        name: "FK_patient_external_identifiers_patients_patient_id",
                        column: x => x.patient_id,
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_history_author",
                table: "medical_record_history_events",
                sql: "(author_kind = 'MedicalWorker' AND performed_by_medical_worker_id IS NOT NULL AND performed_by_service_account_id IS NULL AND event_type <> 'LaboratoryAnalysisResultRecorded') OR (author_kind = 'ServiceAccount' AND performed_by_medical_worker_id IS NULL AND performed_by_service_account_id IS NOT NULL AND event_type IN ('MedicalRecordCreated', 'DiagnosisAdded', 'LaboratoryAnalysisResultRecorded'))");

            migrationBuilder.CreateIndex(
                name: "IX_medical_record_diagnoses_added_by_service_account_id",
                table: "medical_record_diagnoses",
                column: "added_by_service_account_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_author",
                table: "medical_record_diagnoses",
                sql: "(added_by_medical_worker_id IS NOT NULL AND added_by_service_account_id IS NULL) OR (added_by_medical_worker_id IS NULL AND added_by_service_account_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_clinical_time",
                table: "medical_record_diagnoses",
                sql: "(diagnosed_at_utc IS NULL AND diagnosed_at_offset_minutes IS NULL) OR (diagnosed_at_utc IS NOT NULL AND diagnosed_at_offset_minutes IS NOT NULL AND diagnosed_at_offset_minutes BETWEEN -840 AND 840)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_code",
                table: "medical_record_diagnoses",
                sql: "char_length(btrim(diagnosis_code_snapshot)) BETWEEN 3 AND 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_coding_system",
                table: "medical_record_diagnoses",
                sql: "coding_system IS NULL OR char_length(btrim(coding_system)) BETWEEN 1 AND 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_diagnosis_type",
                table: "medical_record_diagnoses",
                sql: "diagnosis_type IS NULL OR char_length(btrim(diagnosis_type)) BETWEEN 1 AND 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_name",
                table: "medical_record_diagnoses",
                sql: "char_length(btrim(diagnosis_name_snapshot)) BETWEEN 3 AND 256");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_service_details",
                table: "medical_record_diagnoses",
                sql: "added_by_service_account_id IS NULL OR (diagnosed_at_utc IS NOT NULL AND coding_system IS NOT NULL AND diagnosis_type IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_diagnosis_definitions_code",
                table: "diagnosis_definitions",
                sql: "char_length(btrim(code)) BETWEEN 3 AND 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_diagnosis_definitions_name",
                table: "diagnosis_definitions",
                sql: "char_length(btrim(name)) BETWEEN 3 AND 256");

            migrationBuilder.CreateIndex(
                name: "IX_patient_external_identifiers_patient_id",
                table: "patient_external_identifiers",
                column: "patient_id");

            migrationBuilder.AddForeignKey(
                name: "FK_medical_record_diagnoses_service_accounts_added_by_service_~",
                table: "medical_record_diagnoses",
                column: "added_by_service_account_id",
                principalTable: "service_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE patient_external_identifiers, medical_record_diagnoses,
                    medical_record_history_events, diagnosis_definitions IN ACCESS EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM patient_external_identifiers)
                        OR EXISTS (SELECT 1 FROM medical_record_diagnoses WHERE
                            added_by_service_account_id IS NOT NULL OR diagnosed_at_utc IS NOT NULL
                            OR coding_system IS NOT NULL OR diagnosis_type IS NOT NULL
                            OR char_length(diagnosis_code_snapshot) <> 6
                            OR char_length(diagnosis_name_snapshot) > 24)
                        OR EXISTS (SELECT 1 FROM diagnosis_definitions WHERE
                            char_length(code) <> 6 OR char_length(name) > 24)
                        OR EXISTS (SELECT 1 FROM medical_record_history_events WHERE
                            author_kind = 'ServiceAccount' AND event_type IN ('MedicalRecordCreated', 'DiagnosisAdded'))
                    THEN
                        RAISE EXCEPTION 'Cannot downgrade HL7 import while imported or expanded diagnosis data exists.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_medical_record_diagnoses_service_accounts_added_by_service_~",
                table: "medical_record_diagnoses");

            migrationBuilder.DropTable(
                name: "patient_external_identifiers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_history_author",
                table: "medical_record_history_events");

            migrationBuilder.DropIndex(
                name: "IX_medical_record_diagnoses_added_by_service_account_id",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_author",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_clinical_time",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_code",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_coding_system",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_diagnosis_type",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_name",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_record_diagnoses_service_details",
                table: "medical_record_diagnoses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_diagnosis_definitions_code",
                table: "diagnosis_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_diagnosis_definitions_name",
                table: "diagnosis_definitions");

            migrationBuilder.DropColumn(
                name: "added_by_service_account_id",
                table: "medical_record_diagnoses");

            migrationBuilder.DropColumn(
                name: "coding_system",
                table: "medical_record_diagnoses");

            migrationBuilder.DropColumn(
                name: "diagnosed_at_offset_minutes",
                table: "medical_record_diagnoses");

            migrationBuilder.DropColumn(
                name: "diagnosed_at_utc",
                table: "medical_record_diagnoses");

            migrationBuilder.DropColumn(
                name: "diagnosis_type",
                table: "medical_record_diagnoses");

            migrationBuilder.AlterColumn<string>(
                name: "diagnosis_name_snapshot",
                table: "medical_record_diagnoses",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "diagnosis_code_snapshot",
                table: "medical_record_diagnoses",
                type: "character varying(6)",
                maxLength: 6,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<Guid>(
                name: "added_by_medical_worker_id",
                table: "medical_record_diagnoses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "diagnosis_definitions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "code",
                table: "diagnosis_definitions",
                type: "character varying(6)",
                maxLength: 6,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_history_author",
                table: "medical_record_history_events",
                sql: "(author_kind = 'MedicalWorker' AND performed_by_medical_worker_id IS NOT NULL AND performed_by_service_account_id IS NULL AND event_type <> 'LaboratoryAnalysisResultRecorded') OR (author_kind = 'ServiceAccount' AND performed_by_medical_worker_id IS NULL AND performed_by_service_account_id IS NOT NULL AND event_type = 'LaboratoryAnalysisResultRecorded')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_code",
                table: "medical_record_diagnoses",
                sql: "char_length(diagnosis_code_snapshot) = 6");

            migrationBuilder.AddCheckConstraint(
                name: "ck_record_diagnoses_name",
                table: "medical_record_diagnoses",
                sql: "char_length(btrim(diagnosis_name_snapshot)) BETWEEN 3 AND 24");

            migrationBuilder.AddCheckConstraint(
                name: "ck_diagnosis_definitions_code",
                table: "diagnosis_definitions",
                sql: "char_length(btrim(code)) = 6");

            migrationBuilder.AddCheckConstraint(
                name: "ck_diagnosis_definitions_name",
                table: "diagnosis_definitions",
                sql: "char_length(btrim(name)) BETWEEN 3 AND 24");
        }
    }
}
