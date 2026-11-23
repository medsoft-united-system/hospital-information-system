using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using HospitalInformationSystem.Application.Contracts.Hl7Import;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.MedicalRecords.Queries.GetMedicalRecord;
using HospitalInformationSystem.Application.MedicalRecords.Queries.GetMedicalRecordSnapshot;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
using HospitalInformationSystem.Application.Patients.Commands.DeletePatient;
using HospitalInformationSystem.Infrastructure.Contexts;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ServiceRole = HospitalInformationSystem.Application.Models.ServiceAccounts.ServiceAccountRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab4PatientImportTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    private static int _nextDiagnosisCode = 1000;
    private static readonly JsonSerializerOptions FixtureJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task NormalizedCorpus_ShouldImportAllPatientsDiagnosesAndCatalogEntriesAndRejectEveryRetry()
    {
        var patients = await ReadCorpusAsync();
        patients.Should().HaveCount(50);
        patients.Sum(patient => patient.Diagnoses.Count).Should().Be(100);
        patients.SelectMany(patient => patient.Diagnoses).Select(diagnosis => diagnosis.Code)
            .Distinct().Should().HaveCount(25);
        var connection = await CreateConnectionAsync();
        var before = await CountsAsync();
        var importedIds = new List<Guid>();

        foreach (var patient in patients)
        {
            var result = await ImportAsync(patient, connection);
            var imported = result.Should().BeOfType<PatientImported>().Subject;
            imported.DiagnosisCount.Should().Be(patient.Diagnoses.Count);
            importedIds.Add(imported.PatientId);
        }

        importedIds.Should().OnlyHaveUniqueItems();
        var after = await CountsAsync();
        after.Patients.Should().Be(before.Patients + 50);
        after.ExternalIdentifiers.Should().Be(before.ExternalIdentifiers + 50);
        after.Records.Should().Be(before.Records + 50);
        after.Diagnoses.Should().Be(before.Diagnoses + 100);
        after.Definitions.Should().Be(before.Definitions + 25);
        after.History.Should().Be(before.History + 150);
        after.Workers.Should().Be(before.Workers);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var externalIds = patients.Select(patient => patient.ExternalPatientId).ToArray();
            var identifiers = await db.PatientExternalIdentifiers.AsNoTracking()
                .Where(row => row.SourceSystem == "LAB4_XML" && externalIds.Contains(row.ExternalPatientId)).ToArrayAsync();
            identifiers.Should().HaveCount(50);
            var expectedDefinitions = patients.SelectMany(patient => patient.Diagnoses)
                .GroupBy(diagnosis => diagnosis.Code).Select(group => group.First()).ToArray();
            var codes = expectedDefinitions.Select(diagnosis => diagnosis.Code).ToArray();
            var definitions = await db.DiagnosisDefinitions.AsNoTracking()
                .Where(row => codes.Contains(row.Code)).ToArrayAsync();
            definitions.Should().HaveCount(25);
            foreach (var definition in expectedDefinitions)
                definitions.Single(row => row.Code == definition.Code).Name.Should().Be(definition.Title);
            for (var index = 0; index < patients.Length; index++)
            {
                var expected = patients[index];
                var identifier = identifiers.Single(row => row.ExternalPatientId == expected.ExternalPatientId);
                identifier.PatientId.Should().Be(importedIds[index]);
                identifier.CreatedAtUtc.Should().Be(connection.ReceivedAtUtc);
                await AssertPatientAsync(db, importedIds[index], expected, connection);
            }
        }
        var complete = await SnapshotAsync();

        foreach (var patient in patients)
        {
            var modified = patient with { FirstName = "Измененное", LastName = "Приповторе" };
            AssertRejection(await ImportAsync(modified, connection), PatientImportRejection.Duplicate);
        }

        (await SnapshotAsync()).Should().Be(complete);
        await AssertAuditOutcomesAsync(connection, Enumerable.Repeat("success", 50)
            .Concat(Enumerable.Repeat("duplicate", 50)).ToArray());
    }

    [Fact]
    public async Task PatientWithoutDiagnoses_ShouldReceiveAnEmptyMedicalRecordAndServiceHistory()
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient();
        var before = await CountsAsync();

        var result = await ImportAsync(patient, connection);

        var imported = result.Should().BeOfType<PatientImported>().Subject;
        imported.DiagnosisCount.Should().Be(0);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await AssertPatientAsync(db, imported.PatientId, patient, connection);
        var after = await CountsAsync();
        after.Patients.Should().Be(before.Patients + 1);
        after.Records.Should().Be(before.Records + 1);
        after.Diagnoses.Should().Be(before.Diagnoses);
        after.Definitions.Should().Be(before.Definitions);
        after.History.Should().Be(before.History + 1);
        after.Workers.Should().Be(before.Workers);
    }

    [Fact]
    public async Task InvalidSecondDiagnosis_ShouldRollbackClinicalWritesAndRecordValidationAudit()
    {
        var connection = await CreateConnectionAsync();
        var first = Diagnosis();
        var second = Diagnosis() with { Title = "" };
        var patient = Patient(first, second);
        var before = await SnapshotAsync();

        var result = await ImportAsync(patient, connection);

        AssertRejection(result, PatientImportRejection.Validation);
        (await SnapshotAsync()).Should().Be(before);
        await AssertAuditOutcomesAsync(connection, "validation-failed");
    }

    [Fact]
    public async Task ConcurrentIdenticalImports_ShouldCommitExactlyOnePatientAndRejectTheDuplicate()
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient(Diagnosis());
        var before = await CountsAsync();

        var results = await Task.WhenAll(ImportAsync(patient, connection), ImportAsync(patient, connection));

        results.OfType<PatientImported>().Should().ContainSingle();
        var duplicate = results.OfType<PatientImportRejected>().Should().ContainSingle().Subject;
        duplicate.Reason.Should().Be(PatientImportRejection.Duplicate);
        var after = await CountsAsync();
        after.Patients.Should().Be(before.Patients + 1);
        after.ExternalIdentifiers.Should().Be(before.ExternalIdentifiers + 1);
        after.Records.Should().Be(before.Records + 1);
        after.Diagnoses.Should().Be(before.Diagnoses + 1);
        after.Definitions.Should().Be(before.Definitions + 1);
        after.History.Should().Be(before.History + 2);
        after.Workers.Should().Be(before.Workers);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await AssertPatientAsync(db, results.OfType<PatientImported>().Single().PatientId, patient, connection);
        await AssertAuditOutcomesAsync(connection, "success", "duplicate");
    }

    [Fact]
    public async Task ConcurrentDifferentPatientsWithTheSameNewCode_ShouldBothImportAndShareTheCatalogDefinition()
    {
        var connection = await CreateConnectionAsync();
        var diagnosis = Diagnosis();
        var first = Patient(diagnosis);
        var second = Patient(diagnosis);
        var before = await CountsAsync();

        var results = await Task.WhenAll(ImportAsync(first, connection), ImportAsync(second, connection));

        results.Should().AllBeOfType<PatientImported>();
        results.Cast<PatientImported>().Select(result => result.PatientId).Should().OnlyHaveUniqueItems();
        var after = await CountsAsync();
        after.Patients.Should().Be(before.Patients + 2);
        after.ExternalIdentifiers.Should().Be(before.ExternalIdentifiers + 2);
        after.Records.Should().Be(before.Records + 2);
        after.Diagnoses.Should().Be(before.Diagnoses + 2);
        after.Definitions.Should().Be(before.Definitions + 1);
        after.History.Should().Be(before.History + 4);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var definition = await db.DiagnosisDefinitions.AsNoTracking().SingleAsync(row => row.Code == diagnosis.Code);
        var ids = results.Cast<PatientImported>().Select(result => result.PatientId).ToArray();
        var recordIds = await db.MedicalRecords.Where(row => ids.Contains(row.PatientId)).Select(row => row.Id).ToArrayAsync();
        (await db.MedicalRecordDiagnoses.AsNoTracking().Where(row => recordIds.Contains(row.MedicalRecordId)).ToArrayAsync())
            .Should().HaveCount(2).And.OnlyContain(row => row.DiagnosisDefinitionId == definition.Id);
    }

    [Fact]
    public async Task ExistingCodeWithDifferentSourceTitle_ShouldKeepCatalogTitleAndPreserveImportSnapshot()
    {
        var connection = await CreateConnectionAsync();
        var diagnosis = Diagnosis();
        var canonicalTitle = "Название справочника " + diagnosis.Code;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.DiagnosisDefinitions.Add(new DiagnosisDefinitionDbo
            {
                Id = Guid.NewGuid(),
                Code = diagnosis.Code,
                Name = canonicalTitle,
                CreatedAtUtc = connection.ReceivedAtUtc,
                UpdatedAtUtc = connection.ReceivedAtUtc
            });
            await db.SaveChangesAsync();
        }
        var patient = Patient(diagnosis);
        var before = await CountsAsync();

        var result = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;

        await using var verification = factory.Services.CreateAsyncScope();
        var after = verification.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await after.DiagnosisDefinitions.AsNoTracking().SingleAsync(row => row.Code == diagnosis.Code))
            .Name.Should().Be(canonicalTitle);
        (await CountsAsync()).Definitions.Should().Be(before.Definitions);
        await AssertPatientAsync(after, result.PatientId, patient, connection);
    }

    [Fact]
    public async Task ExistingTitleWithDifferentCode_ShouldRejectEntirePatientAndKeepCatalogUnchanged()
    {
        var connection = await CreateConnectionAsync();
        var existing = Diagnosis();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.DiagnosisDefinitions.Add(new DiagnosisDefinitionDbo
            {
                Id = Guid.NewGuid(),
                Code = existing.Code,
                Name = existing.Title,
                CreatedAtUtc = connection.ReceivedAtUtc,
                UpdatedAtUtc = connection.ReceivedAtUtc
            });
            await db.SaveChangesAsync();
        }
        var conflicting = Diagnosis() with { Title = existing.Title };
        var patient = Patient(Diagnosis(), conflicting);
        var before = await SnapshotAsync();

        var result = await ImportAsync(patient, connection);

        AssertRejection(result, PatientImportRejection.Validation);
        (await SnapshotAsync()).Should().Be(before);
        await AssertAuditOutcomesAsync(connection, "validation-failed");
    }

    [Fact]
    public async Task RepeatedCodeAtDifferentDates_ShouldPreserveBothClinicalOccurrences()
    {
        var connection = await CreateConnectionAsync();
        var first = Diagnosis();
        var second = first with { DiagnosedAt = first.DiagnosedAt.AddMonths(7) };
        var patient = Patient(first, second);
        var before = await CountsAsync();

        var result = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;

        result.DiagnosisCount.Should().Be(2);
        var after = await CountsAsync();
        after.Definitions.Should().Be(before.Definitions + 1);
        after.Diagnoses.Should().Be(before.Diagnoses + 2);
        await using var scope = factory.Services.CreateAsyncScope();
        await AssertPatientAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), result.PatientId,
            patient, connection);
    }

    [Fact]
    public async Task SameExternalPatientAfterServiceAccountReplacement_ShouldRemainADuplicate()
    {
        var first = await CreateConnectionAsync();
        var patient = Patient(Diagnosis());
        (await ImportAsync(patient, first)).Should().BeOfType<PatientImported>();
        var replacement = await CreateConnectionAsync();
        replacement.ServiceAccount.Id.Should().NotBe(first.ServiceAccount.Id);
        var before = await SnapshotAsync();

        var result = await ImportAsync(patient with { LastName = "Замена" }, replacement);

        AssertRejection(result, PatientImportRejection.Duplicate);
        (await SnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task UnsupportedSourceSystem_ShouldRejectWithoutClinicalWrites()
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient(Diagnosis()) with { SourceSystem = "OTHER_SYSTEM" };
        var before = await SnapshotAsync();

        var result = await ImportAsync(patient, connection);

        AssertRejection(result, PatientImportRejection.Validation);
        (await SnapshotAsync()).Should().Be(before);
        await AssertAuditOutcomesAsync(connection, "validation-failed");
    }

    [Theory]
    [InlineData("lis")]
    [InlineData("disabled")]
    [InlineData("version")]
    public async Task UnauthorizedPersistedServiceAccount_ShouldRejectWithoutClinicalWrites(string reason)
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var row = await db.ServiceAccounts.SingleAsync(account => account.Id == connection.ServiceAccount.Id);
            if (reason == "lis") row.Role = ServiceRole.Lis;
            if (reason == "disabled") row.IsActive = false;
            if (reason == "version") row.AuthenticationVersion++;
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync();

        var action = () => ImportAsync(Patient(Diagnosis()), connection);

        await action.Should().ThrowAsync<ServiceAccountAccessDeniedException>();
        (await SnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task ImportedHistory_ShouldPreserveActualServiceAccountNameAfterItIsRenamed()
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient(Diagnosis());
        var result = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;
        using var administrator = await factory.AdministratorAsync();
        var account = (await administrator.GetFromJsonAsync<ServiceAccountDto>(
            $"/api/service-accounts/{connection.ServiceAccount.Id:D}"))!;
        using var response = await administrator.PutAsJsonAsync($"/api/service-accounts/{account.Id:D}",
            new UpdateServiceAccountRequest("renamed-" + Guid.NewGuid().ToString("N"), account.Version));
        response.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        await AssertPatientAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), result.PatientId,
            patient, connection);
    }

    [Fact]
    public async Task AuditStorageFailureAfterSqlWrite_ShouldRollbackEveryImportWriteAndAllowRetry()
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient(Diagnosis(), Diagnosis());
        var before = await SnapshotAsync(includeAudits: true);
        var failure = new FailAfterImportAuditWrite();
        await using var observed = factory.Application.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddDbContext<HospitalDbContext>(options =>
                options.AddInterceptors(failure))));

        var action = () => ImportAsync(patient, connection, observed.Services);
        await action.Should().ThrowAsync<Exception>();

        failure.Triggered.Should().BeTrue();
        (await SnapshotAsync(includeAudits: true)).Should().Be(before);
        var retry = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;
        retry.DiagnosisCount.Should().Be(2);
        await using var scope = factory.Services.CreateAsyncScope();
        await AssertPatientAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), retry.PatientId,
            patient, connection);
    }

    [Fact]
    public async Task DowngradeWithImportedData_ShouldPreserveMigrationAndAllImportedState()
    {
        const string importMigration = "20261117102104_AddAtomicHl7PatientImport";
        const string previousMigration = "20261116130327_AddHl7ImportServiceRole";
        var connection = await CreateConnectionAsync();
        var patient = Patient(Diagnosis());
        var imported = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;
        var before = await SnapshotAsync(includeAudits: true);
        string[] appliedBefore;
        await using (var migrationScope = factory.Services.CreateAsyncScope())
        {
            var db = migrationScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            appliedBefore = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            appliedBefore.Should().Contain(importMigration);
            var migrate = () => db.GetService<IMigrator>().MigrateAsync(previousMigration);

            var failure = await migrate.Should().ThrowAsync<PostgresException>();

            failure.Which.SqlState.Should().Be("P0001");
            failure.Which.MessageText.Should().Be(
                "Cannot downgrade HL7 import while imported or expanded diagnosis data exists.");
        }

        await using var verification = factory.Services.CreateAsyncScope();
        var after = verification.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await after.Database.GetAppliedMigrationsAsync()).Should().Equal(appliedBefore);
        (await SnapshotAsync(includeAudits: true)).Should().Be(before);
        await AssertPatientAsync(after, imported.PatientId, patient, connection);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SavedExternalPatientIdentifier_ShouldRejectMutationOrDeletion(EntityState state)
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient(Diagnosis());
        var imported = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;
        var before = await SnapshotAsync(includeAudits: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var identifier = await db.PatientExternalIdentifiers.SingleAsync(row => row.PatientId == imported.PatientId);
        db.Entry(identifier).State = state;

        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Сохранённые внешние идентификаторы пациентов неизменяемы.");
        (await SnapshotAsync(includeAudits: true)).Should().Be(before);
    }

    [Fact]
    public async Task ImportedPatientWithMedicalRecord_ShouldRetainTheExistingDeletionRestriction()
    {
        var connection = await CreateConnectionAsync();
        var patient = Patient();
        var result = (await ImportAsync(patient, connection)).Should().BeOfType<PatientImported>().Subject;
        var before = await SnapshotAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var action = () => mediator.Send(new DeletePatientCommand(result.PatientId));

        await action.Should().ThrowAsync<BusinessConflictException>();
        (await SnapshotAsync()).Should().Be(before);
    }

    private async Task<ImportConnectionContext> CreateConnectionAsync()
    {
        using var administrator = await factory.AdministratorAsync();
        using var response = await administrator.PostAsJsonAsync("/api/service-accounts",
            new CreateServiceAccountRequest("l3-import-" + Guid.NewGuid().ToString("N"), "HL7Import"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var account = (await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>())!;
        var tokens = await factory.IssueAsync(account.ApiKey);
        await using var scope = factory.Services.CreateAsyncScope();
        var identity = await scope.ServiceProvider.GetRequiredService<IHl7ImportTokenValidator>()
            .ValidateImportAccessAsync(tokens.AccessToken, CancellationToken.None);
        identity.Should().NotBeNull();
        identity!.Id.Should().Be(account.Account.Id);
        identity.Role.Should().Be(ServiceRole.Hl7Import);
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        return new ImportConnectionContext(identity, new ImportClientCertificate(
            new string('A', 64), "CN=Lab4 integration sender", "CN=Lab4 test CA", "1234567890ABCDEF",
            now.AddDays(-1), now.AddDays(30)), now, "127.0.0.1");
    }

    private Task<ImportPatientResult> ImportAsync(ImportedPatientInput patient, ImportConnectionContext connection,
        IServiceProvider? services = null) => SendAsync(new ImportPatientCommand(
            "L3-" + Guid.NewGuid().ToString("N"), patient, connection), services ?? factory.Services);

    private static async Task<ImportPatientResult> SendAsync(ImportPatientCommand command, IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }

    private static ImportedPatientInput Patient(params ImportedDiagnosisInput[] diagnoses) => new()
    {
        SourceSystem = "LAB4_XML",
        ExternalPatientId = Guid.NewGuid().ToString("N"),
        FirstName = "Алексей",
        LastName = "Проверочный",
        MiddleName = "Петрович",
        BirthDate = new DateOnly(1984, 2, 29),
        Sex = AdministrativeSex.Male,
        Diagnoses = diagnoses
    };

    private static ImportedDiagnosisInput Diagnosis()
    {
        var sequence = Interlocked.Increment(ref _nextDiagnosisCode).ToString("0000");
        var code = "Z" + sequence[..2] + "." + sequence[2..];
        return new ImportedDiagnosisInput
        {
            Code = code,
            Title = "Название импортированного диагноза " + code,
            CodingSystem = "I10",
            DiagnosedAt = new DateTimeOffset(2023, 5, 14, 10, 20, 0, TimeSpan.FromHours(3)),
            DiagnosisType = "U"
        };
    }

    private static async Task<ImportedPatientInput[]> ReadCorpusAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", "normalized-patients.json");
        await using var stream = File.OpenRead(path);
        var fixture = await JsonSerializer.DeserializeAsync<Corpus>(stream, FixtureJson);
        return fixture!.Patients;
    }

    private static void AssertRejection(ImportPatientResult result, PatientImportRejection reason)
    {
        var rejected = result.Should().BeOfType<PatientImportRejected>().Subject;
        rejected.Reason.Should().Be(reason);
        rejected.SafeMessage.Should().NotBeNullOrWhiteSpace();
    }

    private async Task AssertPatientAsync(HospitalDbContext db, Guid id, ImportedPatientInput expected,
        ImportConnectionContext connection)
    {
        var patient = await db.Patients.AsNoTracking().SingleAsync(row => row.Id == id);
        patient.FirstName.Should().Be(expected.FirstName);
        patient.LastName.Should().Be(expected.LastName);
        patient.MiddleName.Should().Be(expected.MiddleName);
        patient.BirthDate.Should().Be(expected.BirthDate);
        patient.Sex.Should().Be((int)expected.Sex);
        var identifier = await db.PatientExternalIdentifiers.AsNoTracking().SingleAsync(row => row.PatientId == id);
        identifier.SourceSystem.Should().Be(expected.SourceSystem);
        identifier.ExternalPatientId.Should().Be(expected.ExternalPatientId);
        var record = await db.MedicalRecords.AsNoTracking().SingleAsync(row => row.PatientId == id);
        var diagnoses = await db.MedicalRecordDiagnoses.AsNoTracking()
            .Where(row => row.MedicalRecordId == record.Id).ToArrayAsync();
        diagnoses.Should().HaveCount(expected.Diagnoses.Count);
        foreach (var diagnosis in expected.Diagnoses)
        {
            var saved = diagnoses.Single(row => row.DiagnosisCodeSnapshot == diagnosis.Code &&
                row.DiagnosedAtUtc == diagnosis.DiagnosedAt.ToUniversalTime());
            saved.DiagnosisNameSnapshot.Should().Be(diagnosis.Title);
            saved.DiagnosedAtUtc.Should().Be(diagnosis.DiagnosedAt.ToUniversalTime());
            saved.DiagnosedAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
            saved.DiagnosedAtOffsetMinutes.Should().Be((int)diagnosis.DiagnosedAt.Offset.TotalMinutes);
            saved.CodingSystem.Should().Be(diagnosis.CodingSystem);
            saved.DiagnosisType.Should().Be(diagnosis.DiagnosisType);
            saved.AddedByMedicalWorkerId.Should().BeNull();
            saved.AddedByServiceAccountId.Should().Be(connection.ServiceAccount.Id);
            saved.DiagnosisDefinitionId.Should().NotBeNull();
            (await db.DiagnosisDefinitions.AsNoTracking().SingleAsync(row => row.Id == saved.DiagnosisDefinitionId))
                .Code.Should().Be(diagnosis.Code);
        }
        var history = await db.MedicalRecordHistoryEvents.AsNoTracking()
            .Where(row => row.MedicalRecordId == record.Id).OrderBy(row => row.Version).ToArrayAsync();
        history.Should().HaveCount(expected.Diagnoses.Count + 1);
        history.Select(row => row.Version).Should().Equal(Enumerable.Range(1, history.Length).Select(value => (long)value));
        history[0].EventType.Should().Be("MedicalRecordCreated");
        history.Skip(1).All(row => row.EventType == "DiagnosisAdded").Should().BeTrue();
        history.Should().OnlyContain(row => row.AuthorKind == MedicalRecordAuthorKind.ServiceAccount &&
            row.PerformedByMedicalWorkerId == null && row.PerformedByServiceAccountId == connection.ServiceAccount.Id &&
            row.PerformedByNameSnapshot == connection.ServiceAccount.Name &&
            row.PerformedByPositionSnapshot == "HL7 Import");
        foreach (var diagnosis in diagnoses)
        {
            var entry = history.Single(row => row.EventType == "DiagnosisAdded" && row.EntityId == diagnosis.Id);
            using var payload = JsonDocument.Parse(entry.PayloadJson);
            var json = payload.RootElement;
            json.GetProperty("code").GetString().Should().Be(diagnosis.DiagnosisCodeSnapshot);
            json.GetProperty("name").GetString().Should().Be(diagnosis.DiagnosisNameSnapshot);
            json.GetProperty("diagnosedAtUtc").GetDateTimeOffset().Should().Be(diagnosis.DiagnosedAtUtc!.Value);
            json.GetProperty("diagnosedAtOffsetMinutes").GetInt32().Should().Be(diagnosis.DiagnosedAtOffsetMinutes!.Value);
            json.GetProperty("codingSystem").GetString().Should().Be(diagnosis.CodingSystem);
            json.GetProperty("diagnosisType").GetString().Should().Be(diagnosis.DiagnosisType);
            json.GetProperty("addedByServiceAccountId").GetGuid().Should().Be(connection.ServiceAccount.Id);
        }
        await using var readScope = factory.Services.CreateAsyncScope();
        var mediator = readScope.ServiceProvider.GetRequiredService<IMediator>();
        var current = await mediator.Send(new GetMedicalRecordQuery(record.Id));
        var snapshot = await mediator.Send(new GetMedicalRecordSnapshotQuery(record.Id,
            history.Max(row => row.OccurredAtUtc)));
        current.Diagnoses.Should().HaveCount(expected.Diagnoses.Count);
        snapshot.Version.Should().Be(record.Version);
        snapshot.Diagnoses.Should().BeEquivalentTo(current.Diagnoses);
        foreach (var diagnosis in diagnoses)
        {
            var currentDiagnosis = current.Diagnoses.Single(row => row.Id == diagnosis.Id);
            currentDiagnosis.Code.Should().Be(diagnosis.DiagnosisCodeSnapshot);
            currentDiagnosis.Name.Should().Be(diagnosis.DiagnosisNameSnapshot);
            currentDiagnosis.DiagnosedAtUtc.Should().Be(diagnosis.DiagnosedAtUtc);
            currentDiagnosis.DiagnosedAtOffsetMinutes.Should().Be(diagnosis.DiagnosedAtOffsetMinutes);
            currentDiagnosis.CodingSystem.Should().Be(diagnosis.CodingSystem);
            currentDiagnosis.DiagnosisType.Should().Be(diagnosis.DiagnosisType);
            currentDiagnosis.AddedByMedicalWorkerId.Should().BeNull();
            currentDiagnosis.AddedByServiceAccountId.Should().Be(connection.ServiceAccount.Id);
        }
        var audits = await db.AuditEvents.AsNoTracking().Where(row => row.EventType == "Hl7PatientImported" &&
            row.ActorServiceAccountId == connection.ServiceAccount.Id).ToArrayAsync();
        var importedAudit = audits.Single(row =>
        {
            using var metadata = JsonDocument.Parse(row.MetadataJson);
            return metadata.RootElement.GetProperty("outcome").GetString() == "success" &&
                metadata.RootElement.GetProperty("targetId").GetGuid() == id;
        });
        importedAudit.ActorKind.Should().Be("service");
        importedAudit.ActorUserId.Should().BeNull();
        importedAudit.ActorDisplayName.Should().Be(connection.ServiceAccount.Name);
        using var importedMetadata = JsonDocument.Parse(importedAudit.MetadataJson);
        var auditJson = importedMetadata.RootElement;
        auditJson.GetProperty("action").GetString().Should().Be("hl7-patient-import");
        auditJson.GetProperty("sourceSystem").GetString().Should().Be(expected.SourceSystem);
        auditJson.GetProperty("externalPatientId").GetString().Should().Be(expected.ExternalPatientId);
        auditJson.GetProperty("diagnosisCount").GetInt32().Should().Be(expected.Diagnoses.Count);
        auditJson.GetProperty("serviceAccountId").GetGuid().Should().Be(connection.ServiceAccount.Id);
        auditJson.GetProperty("clientCertificateSha256").GetString().Should().Be(connection.ClientCertificate.Sha256Fingerprint);
        auditJson.GetProperty("clientCertificateSubject").GetString().Should().Be(connection.ClientCertificate.Subject);
        auditJson.GetProperty("clientCertificateIssuer").GetString().Should().Be(connection.ClientCertificate.Issuer);
        auditJson.GetProperty("clientCertificateSerialNumber").GetString().Should().Be(connection.ClientCertificate.SerialNumber);
        auditJson.GetProperty("clientCertificateNotBeforeUtc").GetDateTimeOffset().Should().Be(connection.ClientCertificate.NotBeforeUtc);
        auditJson.GetProperty("clientCertificateNotAfterUtc").GetDateTimeOffset().Should().Be(connection.ClientCertificate.NotAfterUtc);
    }

    private async Task AssertAuditOutcomesAsync(ImportConnectionContext connection, params string[] expected)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var audits = await db.AuditEvents.AsNoTracking().Where(row => row.EventType == "Hl7PatientImported" &&
            row.ActorServiceAccountId == connection.ServiceAccount.Id).ToArrayAsync();
        var outcomes = audits.Select(row =>
        {
            using var document = JsonDocument.Parse(row.MetadataJson);
            return document.RootElement.GetProperty("outcome").GetString();
        }).ToArray();
        outcomes.Should().BeEquivalentTo(expected);
    }

    private async Task<ClinicalCounts> CountsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return new ClinicalCounts(await db.Patients.CountAsync(), await db.PatientExternalIdentifiers.CountAsync(),
            await db.MedicalRecords.CountAsync(), await db.MedicalRecordDiagnoses.CountAsync(),
            await db.DiagnosisDefinitions.CountAsync(), await db.MedicalRecordHistoryEvents.CountAsync(),
            await db.MedicalWorkers.CountAsync());
    }

    private async Task<DatabaseSnapshot> SnapshotAsync(bool includeAudits = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return new DatabaseSnapshot(
            JsonSerializer.Serialize(await db.Patients.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.PatientExternalIdentifiers.AsNoTracking().OrderBy(row => row.SourceSystem)
                .ThenBy(row => row.ExternalPatientId).ToArrayAsync()),
            JsonSerializer.Serialize(await db.MedicalRecords.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.MedicalRecordDiagnoses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.DiagnosisDefinitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.MedicalRecordHistoryEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            includeAudits
                ? JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().OrderBy(row => row.EventId).ToArrayAsync())
                : "",
            JsonSerializer.Serialize(await db.MedicalWorkers.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    private sealed record Corpus(ImportedPatientInput[] Patients);
    private sealed record ClinicalCounts(int Patients, int ExternalIdentifiers, int Records, int Diagnoses,
        int Definitions, int History, int Workers);
    private sealed record DatabaseSnapshot(string Patients, string ExternalIdentifiers, string Records,
        string Diagnoses, string Definitions, string History, string Audits, string Workers);

    private sealed class FailAfterImportAuditWrite : DbCommandInterceptor
    {
        private int _triggered;
        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            FailAfterAuditWrite(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            FailAfterAuditWrite(command);
            return ValueTask.FromResult(result);
        }

        private void FailAfterAuditWrite(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO security_audit_events", StringComparison.Ordinal) ||
                command.CommandText.Contains("INSERT INTO \"security_audit_events\"", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _triggered, 1);
                throw new InvalidOperationException("Synthetic import audit failure after SQL write and before commit.");
            }
        }
    }
}
