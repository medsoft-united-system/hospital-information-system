using System.Text.Json.Nodes;
using FluentAssertions;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Application.MedicalWorkers.Factories;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.MedicalWorkers;
using ContractStatus = HospitalInformationSystem.Models.MedicalRecords.LaboratoryAnalysisRequestStatus;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryAnalysisSnapshotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RequestedAt = T0.AddMinutes(1);
    private static readonly DateTimeOffset ReceivedAt = T0.AddMinutes(5);
    private static readonly Guid ServiceId = Guid.NewGuid();

    [Fact]
    public void Build_LegacyNumericCode_ShouldPreserveHistoryBeforeAndAfterOrderAndResult()
    {
        var (record, _) = Scenario();
        const string legacyCode = "2345-8";
        var history = record.HistoryEvents.Select(item =>
        {
            if (item.EventType is not (MedicalRecordEventType.LaboratoryAnalysisRequested or
                MedicalRecordEventType.LaboratoryAnalysisResultRecorded)) return item;
            var payload = JsonNode.Parse(item.PayloadJson)!.AsObject();
            payload["loincCode"] = legacyCode;
            return Copy(item, payload: payload.ToJsonString());
        }).ToArray();
        var payloads = history.Select(x => x.PayloadJson).ToArray();

        var before = MedicalRecordSnapshotReplay.Build(record, history, RequestedAt.AddTicks(-1));
        var ordered = MedicalRecordSnapshotReplay.Build(record, history, RequestedAt);
        var completed = MedicalRecordSnapshotReplay.Build(record, history, ReceivedAt);

        before.LaboratoryAnalysisRequests.Should().BeEmpty();
        var pending = ordered.LaboratoryAnalysisRequests.Should().ContainSingle().Subject;
        pending.LoincCode.Should().Be(legacyCode);
        pending.Result.Should().BeNull();
        var saved = completed.LaboratoryAnalysisRequests.Should().ContainSingle().Subject;
        saved.LoincCode.Should().Be(legacyCode);
        saved.Status.Should().Be(ContractStatus.Completed);
        saved.Result!.Value.Should().Be("5.6");
        history.Select(x => x.PayloadJson).Should().Equal(payloads);
    }

    [Fact]
    public void Build_BeforeOrderBetweenOrderAndResultAndAfterResult_ShouldReconstructOnlyHistoricalState()
    {
        var (record, request) = Scenario();
        var historicalRecord = new MedicalRecord
        {
            Id = record.Id, PatientId = record.PatientId, CreatedAtUtc = record.CreatedAtUtc,
            Version = record.Version, HistoryEvents = record.HistoryEvents
        };

        var before = MedicalRecordSnapshotReplay.Build(historicalRecord, historicalRecord.HistoryEvents,
            RequestedAt.AddTicks(-1));
        var ordered = MedicalRecordSnapshotReplay.Build(historicalRecord, historicalRecord.HistoryEvents, RequestedAt);
        var beforeResult = MedicalRecordSnapshotReplay.Build(historicalRecord, historicalRecord.HistoryEvents,
            ReceivedAt.AddTicks(-1));
        var completed = MedicalRecordSnapshotReplay.Build(historicalRecord, historicalRecord.HistoryEvents, ReceivedAt);

        before.LaboratoryAnalysisRequests.Should().BeEmpty();
        before.Version.Should().Be(1);
        ordered.Version.Should().Be(2);
        ordered.LaboratoryAnalysisRequests.Should().ContainSingle(x => x.Id == request.Id &&
            x.Status == ContractStatus.Active && x.Result == null && x.LoincCode == "2345-7" &&
            x.DisplayName == "Глюкоза" && x.Comment == "Натощак");
        beforeResult.LaboratoryAnalysisRequests.Should().ContainSingle(x => x.Result == null);
        completed.Version.Should().Be(3);
        var result = completed.LaboratoryAnalysisRequests.Should().ContainSingle().Subject;
        result.Status.Should().Be(ContractStatus.Completed);
        result.Result!.Value.Should().Be("5.6");
        result.Result.CreatedByServiceAccountId.Should().Be(ServiceId);
        result.Result.IssuedAtUtc.Should().Be(T0.AddSeconds(30));
        result.Result.ReceivedAtUtc.Should().Be(ReceivedAt);
        ordered.LaboratoryAnalysisRequests.Single().Result.Should().BeNull();
    }

    [Fact]
    public void Build_ResultIssuedBeforeOrder_ShouldAppearOnlyAtHisReceivedTime()
    {
        var (record, _) = Scenario();

        var before = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, T0.AddSeconds(30));
        var inFlight = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, RequestedAt.AddMinutes(1));
        var received = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, ReceivedAt);

        before.LaboratoryAnalysisRequests.Should().BeEmpty();
        inFlight.LaboratoryAnalysisRequests.Single().Result.Should().BeNull();
        received.LaboratoryAnalysisRequests.Single().Result.Should().NotBeNull();
    }

    [Fact]
    public void Build_ChangedCatalogAndDeletedRequester_ShouldPreserveOriginalSnapshot()
    {
        var doctor = Doctor();
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", T0);
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), doctor, T0);
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], null,
            doctor, RequestedAt)[0];
        var originalName = $"{doctor.LastName} {doctor.FirstName}";
        definition.LoincCode = "718-7";
        definition.DisplayName = "Изменённый справочник";
        definition.IsActive = false;
        doctor.IsDeleted = true;
        doctor.FirstName = "Другое";
        doctor.LastName = "Имя";

        var snapshot = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, ReceivedAt);

        snapshot.LaboratoryAnalysisRequests.Should().ContainSingle(x => x.Id == request.Id &&
            x.LoincCode == "2345-7" && x.DisplayName == "Глюкоза" && x.RequestedByMedicalWorkerId == doctor.Id &&
            x.RequestedByNameSnapshot == originalName);
    }

    [Fact]
    public void Build_SameAnalysisAssignedAgain_ShouldKeepOrdersSeparate()
    {
        var doctor = Doctor();
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", T0);
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), doctor, T0);
        var first = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], null, doctor, RequestedAt)[0];
        var second = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], null, doctor,
            RequestedAt.AddMinutes(1))[0];
        MedicalRecordService.RecordLaboratoryAnalysisResult(record, first.Id, record.PatientId, "2345-7", "5.6",
            T0, ServiceId, "lis", ReceivedAt);

        var snapshot = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents.Reverse(), ReceivedAt);

        snapshot.LaboratoryAnalysisRequests.Should().HaveCount(2);
        snapshot.LaboratoryAnalysisRequests.Should().Contain(x => x.Id == first.Id && x.Result != null);
        snapshot.LaboratoryAnalysisRequests.Should().Contain(x => x.Id == second.Id && x.Result == null);
    }

    [Theory]
    [InlineData("patientId")]
    [InlineData("medicalRecordId")]
    [InlineData("requestedByMedicalWorkerId")]
    [InlineData("laboratoryTestDefinitionId")]
    [InlineData("authoredAtUtc")]
    [InlineData("loincCode")]
    [InlineData("displayName")]
    [InlineData("comment")]
    public void Build_InvalidRequestPayload_ShouldRejectEvenWhenRequestedSnapshotPrecedesCorruption(string field)
    {
        var (record, _) = Scenario();
        var requested = record.HistoryEvents.ElementAt(1);
        var payload = JsonNode.Parse(requested.PayloadJson)!.AsObject();
        payload[field] = field switch
        {
            "patientId" or "medicalRecordId" or "requestedByMedicalWorkerId" => Guid.NewGuid().ToString(),
            "laboratoryTestDefinitionId" => Guid.Empty.ToString(),
            "authoredAtUtc" => RequestedAt.AddSeconds(1).ToString("O"),
            "loincCode" => "invalid",
            "displayName" => " ",
            _ => "bad\0comment"
        };
        var history = record.HistoryEvents.Select(x => x.Id == requested.Id ? Copy(x, payload: payload.ToJsonString()) : x);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, T0);

        build.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("patientId")]
    [InlineData("laboratoryAnalysisRequestId")]
    [InlineData("createdByServiceAccountId")]
    [InlineData("receivedAtUtc")]
    [InlineData("issuedAtUtc")]
    [InlineData("loincCode")]
    [InlineData("value")]
    public void Build_InvalidResultPayload_ShouldRejectEvenBeforeResultDate(string field)
    {
        var (record, _) = Scenario();
        var result = record.HistoryEvents.Last();
        var payload = JsonNode.Parse(result.PayloadJson)!.AsObject();
        payload[field] = field switch
        {
            "patientId" or "laboratoryAnalysisRequestId" or "createdByServiceAccountId" => Guid.NewGuid().ToString(),
            "receivedAtUtc" => ReceivedAt.AddSeconds(-1).ToString("O"),
            "issuedAtUtc" => T0.AddTicks(1).ToString("O"),
            "loincCode" => "718-7",
            _ => " "
        };
        var history = record.HistoryEvents.Select(x => x.Id == result.Id ? Copy(x, payload: payload.ToJsonString()) : x);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, RequestedAt);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_RequestAndResultWithSameId_ShouldReject()
    {
        var (record, request) = Scenario();
        var result = record.HistoryEvents.Last();
        var payload = JsonNode.Parse(result.PayloadJson)!.AsObject();
        payload["laboratoryAnalysisResultId"] = request.Id.ToString();
        var history = record.HistoryEvents.Select(x => x.Id == result.Id
            ? Copy(x, payload: payload.ToJsonString(), entityId: request.Id) : x);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, ReceivedAt);

        build.Should().Throw<InvalidOperationException>().WithMessage("*идентификатор результата*");
    }

    [Fact]
    public void Build_DuplicateResult_ShouldRejectRatherThanOverwrite()
    {
        var (record, _) = Scenario();
        var result = record.HistoryEvents.Last();
        var duplicate = Copy(result, version: record.Version + 1);
        record.Version++;

        var build = () => MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents.Append(duplicate), ReceivedAt);

        build.Should().Throw<InvalidOperationException>().WithMessage("*повторная регистрация*");
    }

    [Theory]
    [InlineData("human-result")]
    [InlineData("both")]
    [InlineData("none")]
    [InlineData("unknown-kind")]
    [InlineData("service-request")]
    public void Build_MismatchedAuthorKind_ShouldReject(string invalid)
    {
        var (record, _) = Scenario();
        var target = invalid == "service-request" ? record.HistoryEvents.ElementAt(1) : record.HistoryEvents.Last();
        var corrupted = new MedicalRecordHistoryEvent
        {
            Id = target.Id, MedicalRecordId = target.MedicalRecordId, Version = target.Version,
            OccurredAtUtc = target.OccurredAtUtc,
            AuthorKind = invalid switch
            {
                "human-result" => MedicalRecordAuthorKind.MedicalWorker,
                "unknown-kind" => (MedicalRecordAuthorKind)999,
                _ => MedicalRecordAuthorKind.ServiceAccount
            },
            PerformedByMedicalWorkerId = invalid is "human-result" or "both" ? Guid.NewGuid() : null,
            PerformedByServiceAccountId = invalid is "human-result" or "none" ? null : ServiceId,
            PerformedByNameSnapshot = target.PerformedByNameSnapshot,
            PerformedByPositionSnapshot = target.PerformedByPositionSnapshot,
            EventType = target.EventType, EntityId = target.EntityId, PayloadJson = target.PayloadJson
        };
        var history = record.HistoryEvents.Select(x => x.Id == target.Id ? corrupted : x);

        var build = () => MedicalRecordSnapshotReplay.Build(record, history, ReceivedAt);

        build.Should().Throw<InvalidOperationException>().WithMessage("*автор*");
    }

    [Fact]
    public void Build_OldEventsOnly_ShouldKeepNonNullEmptyLaboratoryCollection()
    {
        var doctor = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), doctor, T0);
        var diagnosis = MedicalRecordService.AddDiagnosis(record, Guid.NewGuid(), "I10.00", "Гипертензия", doctor, RequestedAt);
        var prescription = MedicalRecordService.AddPrescription(record, "Контроль давления", doctor, RequestedAt);
        MedicalRecordService.RemoveDiagnosis(record, diagnosis.Id, doctor, ReceivedAt);
        MedicalRecordService.CancelPrescription(record, prescription.Id, doctor, ReceivedAt);

        var snapshot = MedicalRecordSnapshotReplay.Build(record, record.HistoryEvents, ReceivedAt);

        snapshot.Version.Should().Be(5);
        snapshot.LaboratoryAnalysisRequests.Should().NotBeNull().And.BeEmpty();
        snapshot.Diagnoses.Should().BeEmpty();
        snapshot.Prescriptions.Should().BeEmpty();
    }

    private static (MedicalRecord Record, LaboratoryAnalysisRequest Request) Scenario()
    {
        var doctor = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), doctor, T0);
        var definition = LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза", T0);
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], "Натощак", doctor,
            RequestedAt)[0];
        MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId, "2345-7", "5.6",
            T0.AddSeconds(30), ServiceId, "lis", ReceivedAt);
        return (record, request);
    }

    private static MedicalWorker Doctor()
    {
        var doctor = MedicalWorkerFactory.Create("Алексей", "Иванов", "Врач", T0);
        doctor.Role = MedicalWorkerRole.Doctor;
        return doctor;
    }

    private static MedicalRecordHistoryEvent Copy(MedicalRecordHistoryEvent item, string? payload = null,
        Guid? entityId = null, long? version = null) => new()
    {
        Id = Guid.NewGuid(), MedicalRecordId = item.MedicalRecordId, Version = version ?? item.Version,
        OccurredAtUtc = item.OccurredAtUtc, AuthorKind = item.AuthorKind,
        PerformedByMedicalWorkerId = item.PerformedByMedicalWorkerId,
        PerformedByServiceAccountId = item.PerformedByServiceAccountId,
        PerformedByNameSnapshot = item.PerformedByNameSnapshot,
        PerformedByPositionSnapshot = item.PerformedByPositionSnapshot,
        EventType = item.EventType, EntityId = entityId ?? item.EntityId, PayloadJson = payload ?? item.PayloadJson
    };
}
