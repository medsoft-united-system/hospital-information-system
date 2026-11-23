using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.MedicalWorkers.Factories;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.MedicalWorkers;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryAnalysisDomainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServiceId = Guid.NewGuid();

    [Fact]
    public void CreateBatch_ShouldCreateSeparateRequestsAndImmutableCatalogSnapshots()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var glucose = Definition();
        var hemoglobin = Definition("718-7", "Гемоглобин");

        var requests = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [glucose, hemoglobin],
            "  Контроль после лечения  ", author, T0.AddMinutes(1).ToOffset(TimeSpan.FromHours(3)));

        requests.Should().HaveCount(2);
        requests.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        requests.Should().OnlyContain(x => x.MedicalRecordId == record.Id && x.PatientId == record.PatientId &&
            x.RequestedByMedicalWorkerId == author.Id && x.Status == LaboratoryAnalysisRequestStatus.Active &&
            x.Result == null && x.Comment == "Контроль после лечения" && x.AuthoredAtUtc == T0.AddMinutes(1) &&
            x.AuthoredAtUtc.Offset == TimeSpan.Zero);
        record.Version.Should().Be(3);
        record.HistoryEvents.Select(x => x.Version).Should().Equal(1, 2, 3);
        record.HistoryEvents.Skip(1).Should().OnlyContain(x =>
            x.EventType == MedicalRecordEventType.LaboratoryAnalysisRequested &&
            x.AuthorKind == MedicalRecordAuthorKind.MedicalWorker && x.PerformedByMedicalWorkerId == author.Id &&
            x.PerformedByServiceAccountId == null);
        glucose.LoincCode = "1111-1";
        glucose.DisplayName = "Изменённый справочник";
        glucose.IsActive = false;
        requests[0].LoincCodeSnapshot.Should().Be("2345-7");
        requests[0].DisplayNameSnapshot.Should().Be("Глюкоза");
        var payload = JsonSerializer.Deserialize<LaboratoryAnalysisRequestedPayload>(
            record.HistoryEvents.ElementAt(1).PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        payload.LoincCode.Should().Be("2345-7");
        payload.PatientId.Should().Be(record.PatientId);
        payload.RequestedByMedicalWorkerId.Should().Be(author.Id);
    }

    [Fact]
    public void CreateAgain_SameTest_ShouldCreateAnotherIndependentRequest()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var definition = Definition();
        var first = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], null, author, T0)[0];
        var second = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition], " ", author, T0)[0];

        first.Id.Should().NotBe(second.Id);
        second.Comment.Should().BeNull();
        record.LaboratoryAnalysisRequests.Should().HaveCount(2);
        record.Version.Should().Be(3);
    }

    [Fact]
    public void CreateBatch_InactiveLastDefinition_ShouldRejectWholeBatchBeforeMutation()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var inactive = Definition("718-7", "Гемоглобин");
        inactive.IsActive = false;

        var create = () => MedicalRecordService.CreateLaboratoryAnalysisRequests(record,
            [Definition(), inactive], null, author, T0);

        create.Should().Throw<BusinessRuleConflictException>();
        Unchanged(record);
    }

    [Fact]
    public void CreateBatch_LegacyCodeWithWrongChecksum_ShouldRejectNewOrdersBeforeMutation()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var definition = Definition();
        definition.LoincCode = "2345-8";

        var create = () => MedicalRecordService.CreateLaboratoryAnalysisRequests(record,
            [definition], null, author, T0);

        create.Should().Throw<DomainValidationException>();
        Unchanged(record);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(2)]
    public void CreateBatch_EmptyOverLimitOrDuplicateSelection_ShouldReject(int count)
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var same = Definition();
        IReadOnlyList<LaboratoryTestDefinition> definitions = count == 2 ? [same, same] :
            Enumerable.Range(0, count).Select(_ => Definition()).ToArray();

        var create = () => MedicalRecordService.CreateLaboratoryAnalysisRequests(record, definitions, null, author, T0);

        create.Should().Throw<DomainValidationException>();
        Unchanged(record);
    }

    [Theory]
    [InlineData(MedicalWorkerRole.Nurse)]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    [InlineData(MedicalWorkerRole.Other)]
    public void CreateBatch_NonDoctor_ShouldRejectWithoutMutation(MedicalWorkerRole role)
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        author.Role = role;

        var create = () => MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [Definition()], null, author, T0);

        create.Should().Throw<DomainValidationException>();
        Unchanged(record);
    }

    [Fact]
    public void CreateBatch_DeletedDoctor_ShouldRejectWithoutMutation()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        author.IsDeleted = true;
        var create = () => MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [Definition()], null, author, T0);

        create.Should().Throw<DomainValidationException>();
        Unchanged(record);
    }

    [Fact]
    public void CreateBatch_UnicodeCommentBoundary_ShouldCountScalarsAndRejectOverLimitAtomically()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var comment = string.Concat(Enumerable.Repeat("🧪", 2000));
        var create = () => MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [Definition()],
            comment + "А", author, T0);
        create.Should().Throw<DomainValidationException>();
        Unchanged(record);

        MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [Definition()], comment, author, T0)[0]
            .Comment.Should().Be(comment);
    }

    [Fact]
    public void RecordResult_ShouldCompleteOrderWithSeparateIdAndServiceAuthorAtHisTime()
    {
        var (record, request) = Requested();
        var issued = T0.AddSeconds(30).ToOffset(TimeSpan.FromHours(3));
        var received = T0.AddMinutes(5).AddTicks(1234);

        var result = MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, "  Отрицательно  ", issued, ServiceId, "  lab-service  ", received);

        result.Id.Should().NotBe(request.Id).And.NotBeEmpty();
        result.LaboratoryAnalysisRequestId.Should().Be(request.Id);
        result.PatientId.Should().Be(record.PatientId);
        result.Value.Should().Be("Отрицательно");
        result.IssuedAtUtc.Should().Be(issued);
        result.IssuedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        result.ReceivedAtUtc.Should().Be(T0.AddMinutes(5).AddTicks(1230));
        result.CreatedByServiceAccountId.Should().Be(ServiceId);
        request.Status.Should().Be(LaboratoryAnalysisRequestStatus.Completed);
        request.Result.Should().BeSameAs(result);
        record.Version.Should().Be(3);
        var history = record.HistoryEvents.Last();
        history.OccurredAtUtc.Should().Be(result.ReceivedAtUtc);
        history.AuthorKind.Should().Be(MedicalRecordAuthorKind.ServiceAccount);
        history.PerformedByMedicalWorkerId.Should().BeNull();
        history.PerformedByServiceAccountId.Should().Be(ServiceId);
        history.PerformedByNameSnapshot.Should().Be("lab-service");
        history.PerformedByPositionSnapshot.Should().Be("LIS");
        history.EntityId.Should().Be(result.Id);
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("code")]
    [InlineData("service")]
    [InlineData("service-name")]
    public void RecordResult_InvalidIdentity_ShouldNotCompleteOrder(string invalid)
    {
        var (record, request) = Requested();
        var save = () => MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id,
            invalid == "patient" ? Guid.NewGuid() : record.PatientId,
            invalid == "code" ? "718-7" : request.LoincCodeSnapshot, "5.6", T0,
            invalid == "service" ? Guid.Empty : ServiceId, invalid == "service-name" ? " " : "lis", T0.AddMinutes(5));

        save.Should().Throw<DomainValidationException>();
        Uncompleted(record, request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ре\0зультат")]
    public void RecordResult_InvalidValue_ShouldNotCompleteOrder(string? value)
    {
        var (record, request) = Requested();
        var save = () => MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, value!, T0, ServiceId, "lis", T0.AddMinutes(5));

        save.Should().Throw<DomainValidationException>();
        Uncompleted(record, request);
    }

    [Fact]
    public void RecordResult_UnicodeBoundary_ShouldCountScalars()
    {
        var (record, request) = Requested();
        var value = string.Concat(Enumerable.Repeat("🧪", 2000));
        var save = () => MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, value + "А", T0, ServiceId, "lis", T0.AddMinutes(5));
        save.Should().Throw<DomainValidationException>();
        Uncompleted(record, request);

        MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, value, T0, ServiceId, "lis", T0.AddMinutes(5)).Value.Should().Be(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordResult_DefaultOrSubMillisecondIssued_ShouldRejectWithoutMutation(bool subMillisecond)
    {
        var (record, request) = Requested();
        var save = () => MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, "5.6", subMillisecond ? T0.AddTicks(1) : default, ServiceId, "lis", T0);

        save.Should().Throw<DomainValidationException>();
        Uncompleted(record, request);
    }

    [Fact]
    public void RecordResult_Duplicate_ShouldKeepFirstResultAndVersion()
    {
        var (record, request) = Requested();
        var original = MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, "5.6", T0, ServiceId, "lis", T0.AddMinutes(5));
        var repeat = () => MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, "8.1", T0, ServiceId, "lis", T0.AddMinutes(6));

        repeat.Should().Throw<BusinessRuleConflictException>();
        request.Result.Should().BeSameAs(original);
        request.Result!.Value.Should().Be("5.6");
        record.Version.Should().Be(3);
        record.HistoryEvents.Should().HaveCount(3);
    }

    [Fact]
    public void RecordResult_UnknownRequest_ShouldNotModifyOtherOrder()
    {
        var (record, request) = Requested();
        var save = () => MedicalRecordService.RecordLaboratoryAnalysisResult(record, Guid.NewGuid(), record.PatientId,
            request.LoincCodeSnapshot, "5.6", T0, ServiceId, "lis", T0);

        save.Should().Throw<DomainEntityNotFoundException>();
        Uncompleted(record, request);
    }

    [Fact]
    public void RecordResult_AfterClockRollback_ShouldUseMonotonicHisTimeAndPreserveIssued()
    {
        var (record, request) = Requested();
        var result = MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, "5.6", T0.AddMinutes(-1), ServiceId, "lis", T0.AddMinutes(-2));

        result.ReceivedAtUtc.Should().Be(request.AuthoredAtUtc);
        result.IssuedAtUtc.Should().Be(T0.AddMinutes(-1));
        record.HistoryEvents.Last().OccurredAtUtc.Should().Be(result.ReceivedAtUtc);
    }

    [Fact]
    public void LaboratoryTimes_ShouldMatchPostgresMicrosecondsWithoutMovingBeforePreviousEvent()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0.AddTicks(7));
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [Definition()], null,
            author, T0.AddTicks(7))[0];

        request.AuthoredAtUtc.Should().Be(T0.AddTicks(10));
        (request.AuthoredAtUtc.Ticks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        request.AuthoredAtUtc.Should().BeOnOrAfter(record.CreatedAtUtc);
        var result = MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, record.PatientId,
            request.LoincCodeSnapshot, "5.6", T0, ServiceId, "lis", T0.AddMinutes(1).AddTicks(17));
        result.ReceivedAtUtc.Should().Be(T0.AddMinutes(1).AddTicks(10));
        record.HistoryEvents.Last().OccurredAtUtc.Should().Be(result.ReceivedAtUtc);
    }

    private static (MedicalRecord Record, LaboratoryAnalysisRequest Request) Requested()
    {
        var author = Doctor();
        var record = MedicalRecordFactory.Create(Guid.NewGuid(), author, T0);
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [Definition()], null,
            author, T0.AddMinutes(1))[0];
        return (record, request);
    }

    private static MedicalWorker Doctor()
    {
        var worker = MedicalWorkerFactory.Create("Алексей", "Иванов", "Врач", T0);
        worker.Role = MedicalWorkerRole.Doctor;
        return worker;
    }

    private static LaboratoryTestDefinition Definition(string code = "2345-7", string name = "Глюкоза") =>
        LaboratoryTestDefinitionFactory.Create(code, name, T0);

    private static void Unchanged(MedicalRecord record)
    {
        record.LaboratoryAnalysisRequests.Should().BeEmpty();
        record.Version.Should().Be(1);
        record.HistoryEvents.Should().ContainSingle();
    }

    private static void Uncompleted(MedicalRecord record, LaboratoryAnalysisRequest request)
    {
        request.Result.Should().BeNull();
        request.Status.Should().Be(LaboratoryAnalysisRequestStatus.Active);
        record.Version.Should().Be(2);
        record.HistoryEvents.Should().HaveCount(2);
    }
}
