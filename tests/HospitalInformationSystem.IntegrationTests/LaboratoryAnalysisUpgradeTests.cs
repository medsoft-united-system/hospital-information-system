using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LaboratoryAnalysisUpgradeTests
{
    [Fact]
    public async Task MigrateFromL2_ShouldPreserveMedicalDataAndClassifyAllLegacyEventsAsHuman()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("hospital_lab3_upgrade").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261109000515_AddLaboratoryTests");
        var workerId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var diagnosisId = Guid.NewGuid();
        var prescriptionId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO medical_workers (id, first_name, last_name, position, role, is_deleted, created_at_utc, updated_at_utc)
            VALUES ({workerId}, 'Алексей', 'Иванов', 'Врач', 'doctor', false, {at}, {at});
            INSERT INTO patients (id, first_name, last_name, birth_date, sex, created_at_utc, updated_at_utc)
            VALUES ({patientId}, 'Иван', 'Петров', DATE '1990-01-01', 1, {at}, {at});
            INSERT INTO medical_records (id, patient_id, created_at_utc, version)
            VALUES ({recordId}, {patientId}, {at}, 5);
            INSERT INTO diagnosis_definitions (id, code, name, created_at_utc, updated_at_utc)
            VALUES ({definitionId}, 'A00.00', 'Холера', {at}, {at});
            INSERT INTO medical_record_diagnoses (id, medical_record_id, diagnosis_definition_id, diagnosis_code_snapshot,
                diagnosis_name_snapshot, added_at_utc, added_by_medical_worker_id, removed_at_utc, removed_by_medical_worker_id)
            VALUES ({diagnosisId}, {recordId}, {definitionId}, 'A00.00', 'Холера', {at.AddMinutes(1)}, {workerId},
                {at.AddMinutes(2)}, {workerId});
            INSERT INTO prescriptions (id, medical_record_id, text, created_at_utc, created_by_medical_worker_id,
                cancelled_at_utc, cancelled_by_medical_worker_id)
            VALUES ({prescriptionId}, {recordId}, 'Контрольный осмотр', {at.AddMinutes(3)}, {workerId},
                {at.AddMinutes(4)}, {workerId});
            """);
        var payloads = new (string Type, Guid? EntityId, object Payload)[]
        {
            ("MedicalRecordCreated", recordId, new { PatientId = patientId }),
            ("DiagnosisAdded", diagnosisId, new { MedicalRecordDiagnosisId = diagnosisId, DiagnosisDefinitionId = definitionId, Code = "A00.00", Name = "Холера" }),
            ("DiagnosisRemoved", diagnosisId, new { MedicalRecordDiagnosisId = diagnosisId, Code = "A00.00", Name = "Холера" }),
            ("PrescriptionAdded", prescriptionId, new { PrescriptionId = prescriptionId, Text = "Контрольный осмотр", ReplacesPrescriptionId = (Guid?)null }),
            ("PrescriptionCancelled", prescriptionId, new { PrescriptionId = prescriptionId, Text = "Контрольный осмотр" })
        };
        for (var index = 0; index < payloads.Length; index++)
        {
            var item = payloads[index];
            var payload = JsonSerializer.Serialize(item.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO medical_record_history_events (id, medical_record_id, version, occurred_at_utc,
                    performed_by_medical_worker_id, performed_by_name_snapshot, performed_by_position_snapshot,
                    event_type, entity_id, payload_json)
                VALUES ({Guid.NewGuid()}, {recordId}, {index + 1}, {at.AddMinutes(index)}, {workerId},
                    'Иванов Алексей', 'Врач', {item.Type}, {item.EntityId}, {payload}::jsonb)
                """);
        }

        await db.Database.MigrateAsync();

        var history = await db.MedicalRecordHistoryEvents.AsNoTracking().OrderBy(x => x.Version).ToListAsync();
        history.Should().HaveCount(5).And.OnlyContain(x => x.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
            x.PerformedByMedicalWorkerId == workerId && x.PerformedByServiceAccountId == null);
        history.Select(x => x.EventType).Should().Equal(payloads.Select(x => x.Type));
        history.Select(x => x.PayloadJson).Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x));
        (await db.Patients.SingleAsync()).Id.Should().Be(patientId);
        (await db.MedicalRecordDiagnoses.SingleAsync()).Id.Should().Be(diagnosisId);
        (await db.Prescriptions.SingleAsync()).Id.Should().Be(prescriptionId);
        (await db.MedicalRecords.SingleAsync()).Version.Should().Be(5);
        (await db.LaboratoryAnalysisRequests.CountAsync()).Should().Be(0);
        (await db.LaboratoryAnalysisResults.CountAsync()).Should().Be(0);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var record = (await new MedicalRecordRepository(db).FindAsync(recordId, false, CancellationToken.None))!;
        var withDiagnosis = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, at.AddMinutes(1));
        withDiagnosis.Diagnoses.Should().ContainSingle(x => x.Id == diagnosisId && x.Name == "Холера");
        withDiagnosis.LaboratoryAnalysisRequests.Should().BeEmpty();
        var withPrescription = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, at.AddMinutes(3));
        withPrescription.Diagnoses.Should().BeEmpty();
        withPrescription.Prescriptions.Should().ContainSingle(x => x.Id == prescriptionId && x.Text == "Контрольный осмотр");
        withPrescription.LaboratoryAnalysisRequests.Should().BeEmpty();
        var cancelled = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, at.AddMinutes(4));
        cancelled.Prescriptions.Should().BeEmpty();
        cancelled.LaboratoryAnalysisRequests.Should().BeEmpty();
    }
}
