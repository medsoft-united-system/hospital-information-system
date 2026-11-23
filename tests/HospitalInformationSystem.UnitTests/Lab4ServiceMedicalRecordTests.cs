using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Mapping;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Application.MedicalWorkers.Factories;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.UnitTests;

public sealed class Lab4ServiceMedicalRecordTests
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServiceId = Guid.NewGuid();

    [Fact]
    public void CreateForService_ShouldRecordServiceIdentityAndCreationSnapshot()
    {
        var patientId = Guid.NewGuid();
        var record = MedicalRecordFactory.CreateForService(patientId, ServiceId, " XML Import ", ImportedAt);

        record.PatientId.Should().Be(patientId);
        record.Version.Should().Be(1);
        var created = record.HistoryEvents.Should().ContainSingle().Subject;
        created.AuthorKind.Should().Be(MedicalRecordAuthorKind.ServiceAccount);
        created.PerformedByMedicalWorkerId.Should().BeNull();
        created.PerformedByServiceAccountId.Should().Be(ServiceId);
        created.PerformedByNameSnapshot.Should().Be("XML Import");
        created.PerformedByPositionSnapshot.Should().Be("HL7 Import");
        MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, ImportedAt).Version.Should().Be(1);
    }

    [Theory]
    [InlineData(-840)]
    [InlineData(-210)]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(840)]
    public void AddImportedDiagnosis_ShouldPreserveClinicalLocalTimeAndSourceTitle(int offsetMinutes)
    {
        var record = CreateRecord();
        var clinicalTime = new DateTimeOffset(2024, 2, 29, 10, 20, 30, TimeSpan.FromMinutes(offsetMinutes));
        var diagnosis = AddDiagnosis(record, clinicalTime);

        diagnosis.DiagnosisCodeSnapshot.Should().Be("I10");
        diagnosis.DiagnosisNameSnapshot.Should().Be("Исходное название диагноза из XML");
        diagnosis.AddedAtUtc.Should().Be(ImportedAt.AddMinutes(1));
        diagnosis.AddedByMedicalWorkerId.Should().BeNull();
        diagnosis.AddedByServiceAccountId.Should().Be(ServiceId);
        diagnosis.DiagnosedAtUtc.Should().Be(clinicalTime.ToUniversalTime());
        diagnosis.DiagnosedAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
        diagnosis.DiagnosedAtOffsetMinutes.Should().Be(offsetMinutes);
        diagnosis.DiagnosedAtUtc.Value.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).DateTime.Should()
            .Be(clinicalTime.DateTime);
        diagnosis.CodingSystem.Should().Be("I10");
        diagnosis.DiagnosisType.Should().Be("U");

        var current = diagnosis.ToDto();
        var snapshot = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, ImportedAt.AddMinutes(1))
            .Diagnoses.Should().ContainSingle().Subject;
        snapshot.Should().BeEquivalentTo(current);
        var history = record.HistoryEvents.Last();
        history.PerformedByPositionSnapshot.Should().Be("HL7 Import");
        history.PayloadJson.Should().Contain("diagnosedAtOffsetMinutes");
        history.PayloadJson.Should().Contain("addedByServiceAccountId");
    }

    [Fact]
    public void AddImportedDiagnosis_RepeatedCode_ShouldKeepEachOccurrenceAndMonotonicHistory()
    {
        var record = CreateRecord();
        var first = AddDiagnosis(record, ImportedAt.AddYears(-2));
        var second = MedicalRecordService.AddImportedDiagnosis(record, first.DiagnosisDefinitionId!.Value,
            "I10", "Исходное название диагноза из XML", "I10", ImportedAt.AddYears(-1), "U",
            ServiceId, "XML Import", ImportedAt);

        second.Id.Should().NotBe(first.Id);
        record.Diagnoses.Should().HaveCount(2);
        record.HistoryEvents.Select(x => x.Version).Should().Equal(1, 2, 3);
        record.HistoryEvents.Last().OccurredAtUtc.Should().Be(first.AddedAtUtc);
        MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, ImportedAt).Diagnoses.Should().BeEmpty();
        MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, first.AddedAtUtc).Diagnoses.Should()
            .HaveCount(2);
    }

    [Fact]
    public void Build_LegacyHumanPayload_ShouldKeepClinicalDateEmpty()
    {
        var author = MedicalWorkerFactory.Create("Иван", "Иванов", "Врач", ImportedAt);
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, ImportedAt);
        var diagnosis = MedicalRecordService.AddDiagnosis(record, Guid.NewGuid(), "I10.00", "Гипертензия",
            author, ImportedAt.AddMinutes(1));
        var legacyPayload = JsonSerializer.Serialize(new
        {
            medicalRecordDiagnosisId = diagnosis.Id,
            diagnosisDefinitionId = diagnosis.DiagnosisDefinitionId,
            code = diagnosis.DiagnosisCodeSnapshot,
            name = diagnosis.DiagnosisNameSnapshot
        });
        var history = record.HistoryEvents.Select(item => item.Version == 2
            ? CopyEvent(item, legacyPayload, author.Id) : item);

        var snapshot = MedicalRecordSnapshotReplay.Build(record, history, ImportedAt.AddDays(1))
            .Diagnoses.Should().ContainSingle().Subject;

        snapshot.AddedByMedicalWorkerId.Should().Be(author.Id);
        snapshot.AddedByServiceAccountId.Should().BeNull();
        snapshot.DiagnosedAtUtc.Should().BeNull();
        snapshot.DiagnosedAtOffsetMinutes.Should().BeNull();
        snapshot.CodingSystem.Should().BeNull();
        snapshot.DiagnosisType.Should().BeNull();
        snapshot.AddedAtUtc.Should().Be(diagnosis.AddedAtUtc);
    }

    [Theory]
    [InlineData("ab", "Диагноз", "I10", "U")]
    [InlineData("I10", "аб", "I10", "U")]
    [InlineData("I10", "Диагноз", "", "U")]
    [InlineData("I10", "Диагноз", "I10", "")]
    [InlineData("I10", "Диагноз", "I10", "U\0")]
    public void AddImportedDiagnosis_InvalidFields_ShouldPreserveState(string code, string title,
        string system, string type)
    {
        var record = CreateRecord();
        var add = () => MedicalRecordService.AddImportedDiagnosis(record, Guid.NewGuid(), code, title,
            system, ImportedAt.AddYears(-1), type, ServiceId, "XML Import", ImportedAt);

        add.Should().Throw<DomainValidationException>();
        record.Version.Should().Be(1);
        record.Diagnoses.Should().BeEmpty();
        record.HistoryEvents.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("bad\nname")]
    public void AddImportedDiagnosis_InvalidServiceName_ShouldPreserveState(string serviceName)
    {
        var record = CreateRecord();
        var add = () => MedicalRecordService.AddImportedDiagnosis(record, Guid.NewGuid(), "I10", "Диагноз",
            "I10", ImportedAt, "U", ServiceId, serviceName, ImportedAt);

        add.Should().Throw<DomainValidationException>();
        record.Version.Should().Be(1);
        record.Diagnoses.Should().BeEmpty();
    }

    [Fact]
    public void CreateForService_EmptyServiceId_ShouldReject()
    {
        var create = () => MedicalRecordFactory.CreateForService(Guid.NewGuid(), Guid.Empty, "XML Import", ImportedAt);
        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Build_DiagnosisWithBothHistoryAuthors_ShouldReject()
    {
        var record = CreateRecord();
        AddDiagnosis(record, ImportedAt);
        var history = record.HistoryEvents.Select(item => item.Version == 2
            ? CopyEvent(item, item.PayloadJson, Guid.NewGuid()) : item);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, ImportedAt.AddDays(1));

        build.Should().Throw<InvalidOperationException>().WithMessage("*автор*");
    }

    [Fact]
    public void Build_DiagnosisPayloadAuthorMismatch_ShouldReject()
    {
        var record = CreateRecord();
        var diagnosis = AddDiagnosis(record, ImportedAt);
        var payload = new DiagnosisAddedPayload(diagnosis.Id, diagnosis.DiagnosisDefinitionId!.Value,
            diagnosis.DiagnosisCodeSnapshot, diagnosis.DiagnosisNameSnapshot, diagnosis.DiagnosedAtUtc,
            diagnosis.DiagnosedAtOffsetMinutes, diagnosis.CodingSystem, diagnosis.DiagnosisType,
            AddedByServiceAccountId: Guid.NewGuid());
        var history = record.HistoryEvents.Select(item => item.Version == 2
            ? CopyEvent(item, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            : item);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, ImportedAt.AddDays(1));

        build.Should().Throw<InvalidOperationException>().WithMessage("*автор*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-841)]
    [InlineData(841)]
    public void Build_InvalidClinicalOffset_ShouldReject(int? offsetMinutes)
    {
        var record = CreateRecord();
        var diagnosis = AddDiagnosis(record, ImportedAt);
        var payload = new DiagnosisAddedPayload(diagnosis.Id, diagnosis.DiagnosisDefinitionId!.Value,
            diagnosis.DiagnosisCodeSnapshot, diagnosis.DiagnosisNameSnapshot, diagnosis.DiagnosedAtUtc,
            offsetMinutes, diagnosis.CodingSystem, diagnosis.DiagnosisType, AddedByServiceAccountId: ServiceId);
        var history = record.HistoryEvents.Select(item => item.Version == 2
            ? CopyEvent(item, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            : item);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, ImportedAt.AddDays(1));

        build.Should().Throw<InvalidOperationException>().WithMessage("*клиническое время*");
    }

    [Fact]
    public void CreateDiagnosis_ExpandedLimits_ShouldAcceptBoundaries()
    {
        var first = MedicalRecordDiagnosisFactory.Create(Guid.NewGuid(), Guid.NewGuid(), "I10", "Имя",
            Guid.NewGuid(), ImportedAt);
        var second = MedicalRecordDiagnosisFactory.Create(Guid.NewGuid(), Guid.NewGuid(), new string('x', 32),
            new string('я', 256), Guid.NewGuid(), ImportedAt);

        first.DiagnosisCodeSnapshot.Should().Be("I10");
        second.DiagnosisCodeSnapshot.Should().HaveLength(32);
        second.DiagnosisNameSnapshot.Should().HaveLength(256);
        second.DiagnosedAtUtc.Should().BeNull();
    }

    private static MedicalRecord CreateRecord() =>
        MedicalRecordFactory.CreateForService(Guid.NewGuid(), ServiceId, "XML Import", ImportedAt);

    private static MedicalRecordDiagnosis AddDiagnosis(MedicalRecord record, DateTimeOffset clinicalTime) =>
        MedicalRecordService.AddImportedDiagnosis(record, Guid.NewGuid(), " I10 ",
            " Исходное название диагноза из XML ", " I10 ", clinicalTime, " U ", ServiceId,
            "XML Import", ImportedAt.AddMinutes(1));

    private static MedicalRecordHistoryEvent CopyEvent(MedicalRecordHistoryEvent item, string payload,
        Guid? medicalWorkerId = null) => new()
    {
        Id = item.Id, MedicalRecordId = item.MedicalRecordId, Version = item.Version,
        OccurredAtUtc = item.OccurredAtUtc, AuthorKind = item.AuthorKind,
        PerformedByMedicalWorkerId = medicalWorkerId,
        PerformedByServiceAccountId = item.PerformedByServiceAccountId,
        PerformedByNameSnapshot = item.PerformedByNameSnapshot,
        PerformedByPositionSnapshot = item.PerformedByPositionSnapshot,
        EventType = item.EventType, EntityId = item.EntityId, PayloadJson = payload
    };
}
