using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.DI;
using HospitalInformationSystem.Application.MedicalRecords;
using HospitalInformationSystem.Application.MedicalRecords.Commands.CreateLaboratoryAnalysisRequests;
using HospitalInformationSystem.Application.MedicalRecords.Commands.RecordLaboratoryAnalysisResult;
using HospitalInformationSystem.Application.MedicalRecords.Mapping;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Domain.LaboratoryTests.Factories;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalRecords.Factories;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.MedicalWorkers.Factories;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ContractTypes = HospitalInformationSystem.Contracts.MedicalRecords;

namespace HospitalInformationSystem.UnitTests;

public sealed class LaboratoryAnalysisUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateBatch_UsesAuthenticatedDoctorAndServerTimeAndSavesAllRequestsOnce()
    {
        var fixture = new Fixture();
        using var source = new CancellationTokenSource();
        var token = source.Token;
        var result = await fixture.CreateHandler().Handle(new CreateLaboratoryAnalysisRequestsCommand(
            fixture.Record.Id, fixture.Definitions.Select(x => x.Id).ToArray(), " Комментарий врачу лаборатории ",
            fixture.ExpectedVersions), token);

        result.Should().HaveCount(2);
        result.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        result.Should().OnlyContain(x => x.PatientId == fixture.Record.PatientId &&
            x.MedicalRecordId == fixture.Record.Id && x.RequestedByMedicalWorkerId == fixture.Doctor.Id &&
            x.AuthoredAtUtc == Now && x.Status == ContractTypes.LaboratoryAnalysisRequestStatus.Active &&
            x.Result == null && x.Comment == "Комментарий врачу лаборатории");
        result.Select(x => x.LoincCode).Should().Equal("2345-7", "718-7");
        fixture.Record.Version.Should().Be(3);
        fixture.Record.HistoryEvents.Skip(1).Should().OnlyContain(x =>
            x.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
            x.PerformedByMedicalWorkerId == fixture.Doctor.Id && x.PerformedByServiceAccountId == null);
        fixture.Records.Verify(x => x.UpdateAsync(fixture.Record, token), Times.Once);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(token), Times.Once);
        foreach (var definition in fixture.Definitions)
            fixture.Tests.Verify(x => x.FindAsync(definition.Id, false, token), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateBatch_MissingOrInactiveFinalDefinition_DoesNotPartiallyMutateOrSave(bool inactive)
    {
        var fixture = new Fixture();
        var last = fixture.Definitions[^1];
        if (inactive)
            last.IsActive = false;
        else
            fixture.Tests.Setup(x => x.FindAsync(last.Id, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync((LaboratoryTestDefinition?)null);
        var handle = () => fixture.CreateHandler().Handle(new CreateLaboratoryAnalysisRequestsCommand(
            fixture.Record.Id, fixture.Definitions.Select(x => x.Id).ToArray(), null, fixture.ExpectedVersions), CancellationToken.None);

        if (inactive)
            await handle.Should().ThrowAsync<DomainValidationException>();
        else
            await handle.Should().ThrowAsync<EntityNotFoundException>();
        fixture.Record.LaboratoryAnalysisRequests.Should().BeEmpty();
        fixture.Record.Version.Should().Be(1);
        fixture.Record.HistoryEvents.Should().ContainSingle();
        fixture.Records.Verify(x => x.UpdateAsync(It.IsAny<MedicalRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateBatch_StaleFinalDefinition_DoesNotCreateRequestsOrHistory(bool deactivate)
    {
        var fixture = new Fixture();
        var expectedVersions = fixture.ExpectedVersions;
        var changed = fixture.Definitions[^1];
        changed.LoincCode = "5196-1";
        changed.DisplayName = "Исследование, изменённое администратором";
        changed.IsActive = !deactivate;
        changed.Version++;
        var handle = () => fixture.CreateHandler().Handle(new CreateLaboratoryAnalysisRequestsCommand(
            fixture.Record.Id, fixture.Definitions.Select(x => x.Id).ToArray(), null, expectedVersions), CancellationToken.None);

        await handle.Should().ThrowAsync<BusinessConflictException>();
        fixture.Record.LaboratoryAnalysisRequests.Should().BeEmpty();
        fixture.Record.Version.Should().Be(1);
        fixture.Record.HistoryEvents.Should().ContainSingle();
        fixture.Records.Verify(x => x.UpdateAsync(It.IsAny<MedicalRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("missing-key")]
    [InlineData("extra-key")]
    [InlineData("wrong-key")]
    [InlineData("zero")]
    public async Task CreateBatch_InvalidExpectedVersions_RejectsBeforeLoadingOrChangingRecord(string kind)
    {
        var fixture = new Fixture();
        var versions = fixture.ExpectedVersions.ToDictionary(x => x.Key, x => x.Value);
        switch (kind)
        {
            case "empty": versions.Clear(); break;
            case "missing-key": versions.Remove(fixture.Definitions[^1].Id); break;
            case "extra-key": versions.Add(Guid.NewGuid(), 1); break;
            case "wrong-key":
                versions.Remove(fixture.Definitions[^1].Id);
                versions.Add(Guid.NewGuid(), 1);
                break;
            case "zero": versions[fixture.Definitions[^1].Id] = 0; break;
        }
        var command = new CreateLaboratoryAnalysisRequestsCommand(fixture.Record.Id,
            fixture.Definitions.Select(x => x.Id).ToArray(), null, kind == "missing" ? null : versions);
        new CreateLaboratoryAnalysisRequestsCommandValidator().Validate(command).Errors.Should()
            .Contain(x => x.PropertyName == nameof(command.ExpectedVersions));

        var handle = () => fixture.CreateHandler().Handle(command, CancellationToken.None);

        await handle.Should().ThrowAsync<DomainValidationException>();
        fixture.Record.LaboratoryAnalysisRequests.Should().BeEmpty();
        fixture.Record.HistoryEvents.Should().ContainSingle();
        fixture.Record.Version.Should().Be(1);
        fixture.Records.Verify(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Tests.Verify(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(MedicalWorkerRole.Nurse)]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    [InlineData(MedicalWorkerRole.Other)]
    public async Task CreateBatch_NonDoctor_DeniesBeforeLookingUpClinicalRecord(MedicalWorkerRole role)
    {
        var fixture = new Fixture();
        fixture.Doctor.Role = role;
        var handle = () => fixture.CreateHandler().Handle(new CreateLaboratoryAnalysisRequestsCommand(
            fixture.Record.Id, [fixture.Definitions[0].Id], null,
            new Dictionary<Guid, uint> { [fixture.Definitions[0].Id] = fixture.Definitions[0].Version }), CancellationToken.None);

        await handle.Should().ThrowAsync<ClinicalAccessDeniedException>();
        fixture.Records.Verify(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Tests.Verify(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateBatch_DeletedAuthor_DoesNotWrite()
    {
        var fixture = new Fixture();
        fixture.Doctor.IsDeleted = true;
        var handle = () => fixture.CreateHandler().Handle(new CreateLaboratoryAnalysisRequestsCommand(
            fixture.Record.Id, [fixture.Definitions[0].Id], null,
            new Dictionary<Guid, uint> { [fixture.Definitions[0].Id] = fixture.Definitions[0].Version }), CancellationToken.None);

        await handle.Should().ThrowAsync<DomainValidationException>();
        fixture.Record.LaboratoryAnalysisRequests.Should().BeEmpty();
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordResult_DefaultServiceAccessor_DeniesBeforeAnyClinicalLookup()
    {
        var fixture = new Fixture();
        using var services = new ServiceCollection().AddApplication().BuildServiceProvider();
        using var scope = services.CreateScope();
        var handler = new RecordLaboratoryAnalysisResultCommandHandler(fixture.Context, fixture.Records.Object,
            scope.ServiceProvider.GetRequiredService<ICurrentServiceAccount>());
        var handle = () => handler.Handle(new RecordLaboratoryAnalysisResultCommand(Guid.NewGuid(),
            fixture.Record.PatientId, "2345-7", "5.6", Now), CancellationToken.None);

        await handle.Should().ThrowAsync<ServiceAccountAccessDeniedException>();
        fixture.Records.Verify(x => x.FindRecordIdByLaboratoryAnalysisRequestAsync(It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Records.Verify(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordResult_UsesServiceIdentityAndSeparateIssuedAndReceivedTimesAndMapsNestedResult()
    {
        var fixture = new Fixture();
        var request = fixture.AddRequest();
        fixture.ServerNow = Now.AddMinutes(2);
        var issued = Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(3));
        using var source = new CancellationTokenSource();
        var token = source.Token;

        var result = await fixture.ResultHandler().Handle(new RecordLaboratoryAnalysisResultCommand(request.Id,
            fixture.Record.PatientId, " 2345-7 ", " 5.6 ", issued), token);

        result.Id.Should().NotBe(request.Id);
        result.LaboratoryAnalysisRequestId.Should().Be(request.Id);
        result.PatientId.Should().Be(fixture.Record.PatientId);
        result.CreatedByServiceAccountId.Should().Be(fixture.Service.Id);
        result.Value.Should().Be("5.6");
        result.IssuedAtUtc.Should().Be(issued.ToUniversalTime());
        result.IssuedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        result.ReceivedAtUtc.Should().Be(fixture.ServerNow);
        fixture.Records.Verify(x => x.FindRecordIdByLaboratoryAnalysisRequestAsync(request.Id, token), Times.Once);
        fixture.Records.Verify(x => x.UpdateAsync(fixture.Record, token), Times.Once);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(token), Times.Once);
        var mapped = fixture.Record.ToDto().LaboratoryAnalysisRequests.Should().ContainSingle().Subject;
        mapped.Status.Should().Be(ContractTypes.LaboratoryAnalysisRequestStatus.Completed);
        mapped.LoincCode.Should().Be("2345-7");
        mapped.DisplayName.Should().Be("Глюкоза в сыворотке или плазме");
        mapped.Result.Should().BeEquivalentTo(result);
        var history = fixture.Record.HistoryEvents.Last().ToDto();
        history.AuthorKind.Should().Be(ContractTypes.MedicalRecordAuthorKind.ServiceAccount);
        history.PerformedByMedicalWorkerId.Should().BeNull();
        history.PerformedByServiceAccountId.Should().Be(fixture.Service.Id);
        history.PerformedByName.Should().Be(fixture.Service.Name);
        history.OccurredAtUtc.Should().Be(fixture.ServerNow);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordResult_WrongPatientOrCode_DoesNotModifyRequestOrHistory(bool wrongPatient)
    {
        var fixture = new Fixture();
        var request = fixture.AddRequest();
        var version = fixture.Record.Version;
        var handle = () => fixture.ResultHandler().Handle(new RecordLaboratoryAnalysisResultCommand(request.Id,
            wrongPatient ? Guid.NewGuid() : fixture.Record.PatientId, wrongPatient ? "2345-7" : "718-7",
            "Результат", Now), CancellationToken.None);

        await handle.Should().ThrowAsync<DomainValidationException>();
        request.Status.Should().Be(LaboratoryAnalysisRequestStatus.Active);
        request.Result.Should().BeNull();
        fixture.Record.Version.Should().Be(version);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordResult_MissingRequest_DoesNotLoadOrSaveAnyAggregate()
    {
        var fixture = new Fixture();
        fixture.Records.Setup(x => x.FindRecordIdByLaboratoryAnalysisRequestAsync(It.IsAny<Guid>(),
            It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var handle = () => fixture.ResultHandler().Handle(new RecordLaboratoryAnalysisResultCommand(Guid.NewGuid(),
            fixture.Record.PatientId, "2345-7", "5.6", Now), CancellationToken.None);

        await handle.Should().ThrowAsync<EntityNotFoundException>();
        fixture.Records.Verify(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordResult_RepeatedSubmission_DoesNotOverwriteOrSaveAgain()
    {
        var fixture = new Fixture();
        var request = fixture.AddRequest();
        var command = new RecordLaboratoryAnalysisResultCommand(request.Id, fixture.Record.PatientId, "2345-7", "5.6", Now);
        var original = await fixture.ResultHandler().Handle(command, CancellationToken.None);
        var version = fixture.Record.Version;
        var repeat = () => fixture.ResultHandler().Handle(command with { Value = "7.8" }, CancellationToken.None);

        await repeat.Should().ThrowAsync<BusinessRuleConflictException>();
        request.Result!.Id.Should().Be(original.Id);
        request.Result.Value.Should().Be("5.6");
        fixture.Record.Version.Should().Be(version);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void CreateValidator_RejectsMissingDuplicatedEmptyAndOversizedSelections()
    {
        var validator = new CreateLaboratoryAnalysisRequestsCommandValidator();
        var id = Guid.NewGuid();
        foreach (var selection in new IReadOnlyList<Guid>?[]
                 {
                     null, [], [id, id], [Guid.Empty], Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray()
                 })
            validator.Validate(new CreateLaboratoryAnalysisRequestsCommand(Guid.NewGuid(), selection!, null))
                .Errors.Should().Contain(x => x.PropertyName == "LaboratoryTestDefinitionIds");
        validator.Validate(new CreateLaboratoryAnalysisRequestsCommand(Guid.Empty, [id], null)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validators_EnforceUnicodeTextAndMillisecondPrecisionBoundaries()
    {
        var create = new CreateLaboratoryAnalysisRequestsCommandValidator();
        var result = new RecordLaboratoryAnalysisResultCommandValidator();
        var unicode = string.Concat(Enumerable.Repeat("🧪", 2000));
        var definitionId = Guid.NewGuid();
        var batch = new CreateLaboratoryAnalysisRequestsCommand(Guid.NewGuid(), [definitionId], " " + unicode + " ",
            new Dictionary<Guid, uint> { [definitionId] = 1 });
        create.Validate(batch).IsValid.Should().BeTrue();
        create.Validate(batch with { Comment = unicode + "а" }).IsValid.Should().BeFalse();
        create.Validate(batch with { Comment = "Комментарий\0" }).IsValid.Should().BeFalse();
        var command = new RecordLaboratoryAnalysisResultCommand(Guid.NewGuid(), Guid.NewGuid(), "2345-7", unicode, Now);
        result.Validate(command).IsValid.Should().BeTrue();
        result.Validate(command with { LoincCode = "2345-8" }).IsValid.Should().BeTrue();
        result.Validate(command with { Value = unicode + "а" }).IsValid.Should().BeFalse();
        result.Validate(command with { Value = " " }).IsValid.Should().BeFalse();
        result.Validate(command with { Value = "Результат\0" }).IsValid.Should().BeFalse();
        result.Validate(command with { IssuedAtUtc = Now.AddTicks(1) }).IsValid.Should().BeFalse();
        result.Validate(command with { IssuedAtUtc = default }).IsValid.Should().BeFalse();
        result.Validate(command with { LoincCode = "INVALID" }).IsValid.Should().BeFalse();
        result.Validate(command with { RequestId = Guid.Empty }).IsValid.Should().BeFalse();
        result.Validate(command with { PatientId = Guid.Empty }).IsValid.Should().BeFalse();
    }

    private sealed class Fixture
    {
        public Mock<IMedicalRecordRepository> Records { get; } = new();
        public Mock<IMedicalWorkerRepository> Workers { get; } = new();
        public Mock<ILaboratoryTestRepository> Tests { get; } = new();
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();
        public Mock<ICurrentMedicalWorker> CurrentWorker { get; } = new();
        public Mock<ICurrentServiceAccount> CurrentService { get; } = new();
        public MedicalWorker Doctor { get; } = MedicalWorkerFactory.Create("Лев", "Иванов", "Врач", Now.AddDays(-1), MedicalWorkerRole.Doctor);
        public ServiceAccountIdentity Service { get; } = new(Guid.NewGuid(), "go-lis", 3);
        public MedicalRecord Record { get; }
        public LaboratoryTestDefinition[] Definitions { get; } =
        [
            LaboratoryTestDefinitionFactory.Create("2345-7", "Глюкоза в сыворотке или плазме", Now.AddDays(-1)),
            LaboratoryTestDefinitionFactory.Create("718-7", "Гемоглобин в крови", Now.AddDays(-1))
        ];
        public DateTimeOffset ServerNow { get; set; } = Now;
        public IReadOnlyDictionary<Guid, uint> ExpectedVersions => Definitions.ToDictionary(x => x.Id, x => x.Version);
        public MedicalRecordCommandContext Context { get; }

        public Fixture()
        {
            Record = MedicalRecordFactory.Create(Guid.NewGuid(), Doctor, Now.AddDays(-1));
            var clock = new Mock<TimeProvider>();
            clock.Setup(x => x.GetUtcNow()).Returns(() => ServerNow);
            Context = new MedicalRecordCommandContext(Records.Object, Workers.Object, UnitOfWork.Object, clock.Object);
            Workers.Setup(x => x.FindAsync(Doctor.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(Doctor);
            Records.Setup(x => x.FindAsync(Record.Id, true, It.IsAny<CancellationToken>())).ReturnsAsync(Record);
            Records.Setup(x => x.FindRecordIdByLaboratoryAnalysisRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Record.Id);
            UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            CurrentWorker.Setup(x => x.RequireId()).Returns(Doctor.Id);
            CurrentService.Setup(x => x.RequireIdentity()).Returns(Service);
            foreach (var definition in Definitions)
            {
                definition.Version = 1;
                Tests.Setup(x => x.FindAsync(definition.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(definition);
            }
        }

        public CreateLaboratoryAnalysisRequestsCommandHandler CreateHandler() => new(Context, Tests.Object, CurrentWorker.Object);
        public RecordLaboratoryAnalysisResultCommandHandler ResultHandler() => new(Context, Records.Object, CurrentService.Object);
        public LaboratoryAnalysisRequest AddRequest() => MedicalRecordService.CreateLaboratoryAnalysisRequests(
            Record, [Definitions[0]], null, Doctor, Now).Single();
    }
}
