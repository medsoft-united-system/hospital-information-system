using System.Security.Cryptography;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Domain.LaboratoryTests.Factories;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalRecords.Factories;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.MedicalWorkers.Factories;
using HospitalInformationSystem.Domain.ServiceAccounts.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LaboratoryAnalysisPersistenceTests(HospitalApiFactory factory) : IClassFixture<HospitalApiFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Repository_ShouldRoundTripNestedResultsAndPreserveThemDuringUnrelatedChanges()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>();
        var state = await CreateStateAsync(scope.ServiceProvider);
        var record = (await repository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        var requests = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [state.Definition], " Комментарий ",
            state.Doctor, T0.AddMinutes(1).AddTicks(7));
        await repository.UpdateAsync(record, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        record = (await repository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        var request = record.LaboratoryAnalysisRequests.Single();
        request.Id.Should().Be(requests.Single().Id);
        request.Comment.Should().Be("Комментарий");
        request.Result.Should().BeNull();
        var result = MedicalRecordService.RecordLaboratoryAnalysisResult(record, request.Id, state.PatientId,
            request.LoincCodeSnapshot, " 142 🧪 ", T0.AddMinutes(2), state.ServiceId, state.ServiceName,
            T0.AddMinutes(3).AddTicks(7));
        await repository.UpdateAsync(record, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var catalog = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == state.Definition.Id);
        catalog.DisplayName = "Изменённое название из справочника";
        catalog.IsActive = false;
        catalog.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        record = (await repository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        MedicalRecordService.AddPrescription(record, "Контрольный осмотр", state.Doctor, T0.AddMinutes(4));
        await repository.UpdateAsync(record, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = (await repository.FindAsync(state.RecordId, false, CancellationToken.None))!;
        var savedRequest = saved.LaboratoryAnalysisRequests.Single();
        savedRequest.DisplayNameSnapshot.Should().Be(state.Definition.DisplayName);
        savedRequest.Status.Should().Be(LaboratoryAnalysisRequestStatus.Completed);
        savedRequest.Result.Should().BeEquivalentTo(result);
        savedRequest.Result!.Value.Should().Be("142 🧪");
        saved.HistoryEvents.Select(x => x.Version).Order().Should().Equal(1, 2, 3, 4);
        var resultEvent = saved.HistoryEvents.Single(x => x.EventType == MedicalRecordEventType.LaboratoryAnalysisResultRecorded);
        resultEvent.AuthorKind.Should().Be(MedicalRecordAuthorKind.ServiceAccount);
        resultEvent.PerformedByMedicalWorkerId.Should().BeNull();
        resultEvent.PerformedByServiceAccountId.Should().Be(state.ServiceId);
        resultEvent.PerformedByNameSnapshot.Should().Be(state.ServiceName);
        resultEvent.PerformedByPositionSnapshot.Should().Be("LIS");
        resultEvent.OccurredAtUtc.Should().Be(savedRequest.Result.ReceivedAtUtc);
        MedicalRecordSnapshotReplay.Build(saved, saved.HistoryEvents, T0.AddSeconds(30))
            .LaboratoryAnalysisRequests.Should().BeEmpty();
        MedicalRecordSnapshotReplay.Build(saved, saved.HistoryEvents, T0.AddMinutes(2))
            .LaboratoryAnalysisRequests.Single().Result.Should().BeNull();
        MedicalRecordSnapshotReplay.Build(saved, saved.HistoryEvents, T0.AddMinutes(3))
            .LaboratoryAnalysisRequests.Single().Result!.Value.Should().Be("142 🧪");
        (await repository.FindRecordIdByLaboratoryAnalysisRequestAsync(request.Id, CancellationToken.None))
            .Should().Be(state.RecordId);
        (await db.LaboratoryAnalysisResults.CountAsync(x => x.LaboratoryAnalysisRequestId == request.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Request_WithAnotherPatient_ShouldBeRejectedByCompositeMedicalRecordForeignKey()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var state = await CreateStateAsync(scope.ServiceProvider);
        var other = NewPatient();
        db.Patients.Add(other);
        await db.SaveChangesAsync();
        db.LaboratoryAnalysisRequests.Add(new LaboratoryAnalysisRequestDbo
        {
            Id = Guid.NewGuid(), MedicalRecordId = state.RecordId, PatientId = other.Id,
            RequestedByMedicalWorkerId = state.Doctor.Id, LaboratoryTestDefinitionId = state.Definition.Id,
            LoincCodeSnapshot = state.Definition.LoincCode, DisplayNameSnapshot = state.Definition.DisplayName,
            AuthoredAtUtc = T0.AddMinutes(1)
        });

        await AssertSaveConflictAsync(db, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData("patient", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("same-id", PostgresErrorCodes.CheckViolation)]
    [InlineData("empty-value", PostgresErrorCodes.CheckViolation)]
    [InlineData("issued-precision", PostgresErrorCodes.CheckViolation)]
    [InlineData("service", PostgresErrorCodes.ForeignKeyViolation)]
    public async Task Result_InvalidRelationalOrValueState_ShouldBeRejected(string invalid, string sqlState)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var state = await CreateStateAsync(scope.ServiceProvider);
        var requestId = await AddRequestAsync(scope.ServiceProvider, state);
        var result = NewResult(requestId, state);
        switch (invalid)
        {
            case "patient": result.PatientId = Guid.NewGuid(); break;
            case "same-id": result.Id = requestId; break;
            case "empty-value": result.Value = " "; break;
            case "issued-precision": result.IssuedAtUtc = result.IssuedAtUtc.AddTicks(10); break;
            case "service": result.CreatedByServiceAccountId = Guid.NewGuid(); break;
        }
        db.LaboratoryAnalysisResults.Add(result);

        await AssertSaveConflictAsync(db, sqlState);
    }

    [Fact]
    public async Task Result_SecondResultForSameRequest_ShouldBeRejectedByUniqueConstraint()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var state = await CreateStateAsync(scope.ServiceProvider);
        var requestId = await AddRequestAsync(scope.ServiceProvider, state);
        db.LaboratoryAnalysisResults.Add(NewResult(requestId, state));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.LaboratoryAnalysisResults.Add(NewResult(requestId, state));

        await AssertSaveConflictAsync(db, PostgresErrorCodes.UniqueViolation);
        (await db.LaboratoryAnalysisResults.CountAsync(x => x.LaboratoryAnalysisRequestId == requestId)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_ExistingResult_ShouldRejectTrackedChangeOrDeletion(bool delete)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var state = await CreateStateAsync(scope.ServiceProvider);
        var requestId = await AddRequestAsync(scope.ServiceProvider, state);
        var result = NewResult(requestId, state);
        db.LaboratoryAnalysisResults.Add(result);
        await db.SaveChangesAsync();
        if (delete) db.LaboratoryAnalysisResults.Remove(result);
        else result.Value = "Подмена результата";

        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*результаты*нельзя*");
    }

    [Theory]
    [InlineData("both")]
    [InlineData("none")]
    [InlineData("service-human-event")]
    [InlineData("human-result")]
    public async Task History_InvalidAuthorUnion_ShouldBeRejected(string invalid)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var state = await CreateStateAsync(scope.ServiceProvider);
        var history = new MedicalRecordHistoryEventDbo
        {
            Id = Guid.NewGuid(), MedicalRecordId = state.RecordId, Version = 2, OccurredAtUtc = T0.AddMinutes(1),
            AuthorKind = MedicalRecordAuthorKind.ServiceAccount, PerformedByServiceAccountId = state.ServiceId,
            PerformedByNameSnapshot = state.ServiceName, PerformedByPositionSnapshot = "LIS",
            EventType = "LaboratoryAnalysisResultRecorded", PayloadJson = "{}"
        };
        switch (invalid)
        {
            case "both": history.PerformedByMedicalWorkerId = state.Doctor.Id; break;
            case "none": history.PerformedByServiceAccountId = null; break;
            case "service-human-event": history.EventType = "DiagnosisAdded"; break;
            case "human-result":
                history.AuthorKind = MedicalRecordAuthorKind.MedicalWorker;
                history.PerformedByMedicalWorkerId = state.Doctor.Id;
                history.PerformedByServiceAccountId = null;
                break;
        }
        db.MedicalRecordHistoryEvents.Add(history);

        await AssertSaveConflictAsync(db, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task ConcurrentResultRegistration_ShouldCommitExactlyOneResultAndEvent()
    {
        await using var setupScope = factory.Services.CreateAsyncScope();
        var state = await CreateStateAsync(setupScope.ServiceProvider);
        var requestId = await AddRequestAsync(setupScope.ServiceProvider, state);
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>();
        var first = (await firstRepository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        var second = (await secondRepository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        foreach (var record in new[] { first, second })
            MedicalRecordService.RecordLaboratoryAnalysisResult(record, requestId, state.PatientId,
                state.Definition.LoincCode, "142", T0.AddMinutes(2), state.ServiceId, state.ServiceName, T0.AddMinutes(3));
        await firstRepository.UpdateAsync(first, CancellationToken.None);
        await secondRepository.UpdateAsync(second, CancellationToken.None);
        await firstScope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync();

        var save = () => secondScope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync();

        await save.Should().ThrowAsync<BusinessConflictException>();
        var saved = (await firstRepository.FindAsync(state.RecordId, false, CancellationToken.None))!;
        saved.Version.Should().Be(3);
        saved.HistoryEvents.Should().ContainSingle(x => x.EventType == MedicalRecordEventType.LaboratoryAnalysisResultRecorded);
        saved.LaboratoryAnalysisRequests.Single().Result.Should().NotBeNull();
    }

    private static async Task<State> CreateStateAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<HospitalDbContext>();
        var doctor = MedicalWorkerFactory.Create("Алексей", "Проверка", "Врач", T0, MedicalWorkerRole.Doctor);
        var patient = NewPatient();
        var service = ServiceAccountFactory.Create(("lis-" + Guid.NewGuid().ToString("N")).PadRight(64, 'S'),
            SHA256.HashData("test-only-laboratory-key"u8), T0);
        db.MedicalWorkers.Add(new MedicalWorkerDbo
        {
            Id = doctor.Id, FirstName = doctor.FirstName, LastName = doctor.LastName, Position = doctor.Position,
            Role = doctor.Role, CreatedAtUtc = T0, UpdatedAtUtc = T0
        });
        db.Patients.Add(patient);
        db.ServiceAccounts.Add(new ServiceAccountDbo
        {
            Id = service.Id, Name = service.Name, NormalizedName = service.NormalizedName, Role = service.Role,
            ApiKeyHash = service.ApiKeyHash, AuthenticationVersion = service.AuthenticationVersion,
            CreatedAtUtc = T0, UpdatedAtUtc = T0
        });
        var domainDefinition = LaboratoryTestDefinitionFactory.Create(
            NewCode(),
            "Синтетическое исследование для проверки хранения", T0);
        db.LaboratoryTestDefinitions.Add(new LaboratoryTestDefinitionDbo
        {
            Id = domainDefinition.Id, LoincCode = domainDefinition.LoincCode, DisplayName = domainDefinition.DisplayName,
            IsActive = true, CreatedAtUtc = T0, UpdatedAtUtc = T0
        });
        await db.SaveChangesAsync();
        var record = MedicalRecordFactory.Create(patient.Id, doctor, T0);
        services.GetRequiredService<IMedicalRecordRepository>().Add(record);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new State(record.Id, patient.Id, doctor, domainDefinition, service.Id, service.Name);
    }

    private static async Task<Guid> AddRequestAsync(IServiceProvider services, State state)
    {
        var repository = services.GetRequiredService<IMedicalRecordRepository>();
        var record = (await repository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [state.Definition], null,
            state.Doctor, T0.AddMinutes(1)).Single();
        await repository.UpdateAsync(record, CancellationToken.None);
        await services.GetRequiredService<HospitalDbContext>().SaveChangesAsync();
        return request.Id;
    }

    private static LaboratoryAnalysisResultDbo NewResult(Guid requestId, State state) => new()
    {
        Id = Guid.NewGuid(), LaboratoryAnalysisRequestId = requestId, PatientId = state.PatientId,
        Value = "142", IssuedAtUtc = T0.AddMinutes(2), ReceivedAtUtc = T0.AddMinutes(3),
        CreatedByServiceAccountId = state.ServiceId
    };

    private static string NewCode()
    {
        var number = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray())
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var index = number.Length - 1; index >= 0; index--)
        {
            var digit = number[index] - '0';
            if ((number.Length - index) % 2 == 1)
                digit *= 2;
            sum += digit / 10 + digit % 10;
        }
        return number + "-" + (10 - sum % 10) % 10;
    }

    private static PatientDbo NewPatient() => new()
    {
        Id = Guid.NewGuid(), FirstName = "Пациент", LastName = "Лаборатории", BirthDate = new DateOnly(1990, 1, 1),
        Sex = 1, CreatedAtUtc = T0, UpdatedAtUtc = T0
    };

    private static async Task AssertSaveConflictAsync(HospitalDbContext db, string sqlState)
    {
        var save = () => db.SaveChangesAsync();
        var exception = (await save.Should().ThrowAsync<BusinessConflictException>()).Which;
        exception.GetBaseException().Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(sqlState);
    }

    private sealed record State(Guid RecordId, Guid PatientId, MedicalWorker Doctor,
        LaboratoryTestDefinition Definition, Guid ServiceId, string ServiceName);
}
