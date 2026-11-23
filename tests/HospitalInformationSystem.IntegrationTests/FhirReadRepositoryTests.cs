using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Models.Fhir;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirReadRepositoryTests(HospitalApiFactory factory, ITestOutputHelper output) : IClassFixture<HospitalApiFactory>
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Kathmandu")]
    [InlineData("America/St_Johns")]
    public async Task ServiceRequestPages_ShouldTraverseAllRowsInWireTimestampAndUuidOrder(string timeZone)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('TimeZone', {timeZone}, true)");
        var state = await SeedAsync(db, 45);
        var repository = scope.ServiceProvider.GetRequiredService<IFhirReadRepository>();
        var ids = new List<Guid>();
        var pageSizes = new List<int>();
        FhirPagePosition? after = null;
        do
        {
            var page = await repository.SearchServiceRequestsAsync(null, 20, after, CancellationToken.None);
            pageSizes.Add(page.Items.Count);
            ids.AddRange(page.Items.Select(x => x.Id));
            if (page.Next is { } next)
            {
                next.AtUtc.Should().Be(Milliseconds(page.Items[^1].AuthoredAtUtc));
                next.Id.Should().Be(page.Items[^1].Id);
                (next.AtUtc.UtcTicks % TimeSpan.TicksPerMillisecond).Should().Be(0);
            }
            after = page.Next;
        } while (after is not null && pageSizes.Count < 5);

        pageSizes.Should().Equal(20, 20, 5);
        ids.Should().OnlyHaveUniqueItems().And.Equal(state.Requests.OrderBy(x => Milliseconds(x.AuthoredAtUtc))
            .ThenBy(x => x.Id).Select(x => x.Id));
        after.Should().BeNull();
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceRequestPages_ShouldSeekThroughMatchingIndexesWithoutSorting()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var state = await SeedAsync(db, 6000);
        await db.Database.ExecuteSqlRawAsync("ANALYZE laboratory_analysis_requests");
        var probe = new SearchCommandProbe();
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(db.Database.GetDbConnection()).AddInterceptors(probe).Options;
        await using var searchDb = new HospitalDbContext(options);
        await searchDb.Database.UseTransactionAsync(transaction.GetDbTransaction());
        var repository = new FhirReadRepository(searchDb);
        var middle = state.Requests[3000];
        var position = new FhirPagePosition(Milliseconds(middle.AuthoredAtUtc), middle.Id);

        foreach (var status in new LaboratoryAnalysisRequestStatus?[]
                 { null, LaboratoryAnalysisRequestStatus.Active, LaboratoryAnalysisRequestStatus.Completed })
            foreach (var after in new FhirPagePosition?[] { null, position })
            {
                await repository.SearchServiceRequestsAsync(status, 20, after, CancellationToken.None);
                await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + probe.CommandText,
                    (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
                foreach (var parameter in probe.Parameters)
                    explain.Parameters.Add(parameter.Clone());
                var planJson = (string)(await explain.ExecuteScalarAsync())!;
                output.WriteLine($"status={status?.ToString() ?? "all"}, continuation={after is not null}\n{planJson}");
                using var document = JsonDocument.Parse(planJson);
                var nodes = PlanNodes(document.RootElement[0].GetProperty("Plan")).ToArray();
                nodes.Should().NotContain(x => x.GetProperty("Node Type").GetString() == "Sort");
                var indexScan = nodes.Where(x => x.TryGetProperty("Index Name", out var name) &&
                        (name.GetString() == "ix_lab_requests_fhir_order" ||
                         status.HasValue && name.GetString() == "ix_lab_requests_queue"))
                    .Should().ContainSingle().Which;
                if (status.HasValue && indexScan.GetProperty("Index Name").GetString() == "ix_lab_requests_queue")
                    indexScan.GetProperty("Index Cond").GetString().Should().Contain("status");
                if (after is not null)
                    indexScan.GetProperty("Index Cond").GetString().Should()
                        .Contain("authored_at_fhir_utc").And.Contain("id").And.Contain(">");
            }
    }

    [Theory]
    [InlineData(LaboratoryAnalysisRequestStatus.Active)]
    [InlineData(LaboratoryAnalysisRequestStatus.Completed)]
    public async Task ServiceRequestPages_ShouldKeepStatusFilterOnEveryPage(LaboratoryAnalysisRequestStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var state = await SeedAsync(db, 24);
        var repository = scope.ServiceProvider.GetRequiredService<IFhirReadRepository>();
        var items = new List<FhirServiceRequestReadModel>();
        FhirPagePosition? after = null;
        var pageCount = 0;
        do
        {
            var page = await repository.SearchServiceRequestsAsync(status, 5, after, CancellationToken.None);
            items.AddRange(page.Items);
            after = page.Next;
            pageCount++;
        } while (after is not null && pageCount < 10);

        items.Should().OnlyContain(x => x.Status == status);
        items.Select(x => x.Id).Should().Equal(state.Requests.Where(x => x.Status == status)
            .OrderBy(x => Milliseconds(x.AuthoredAtUtc)).ThenBy(x => x.Id).Select(x => x.Id));
        items.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        after.Should().BeNull();
    }

    [Fact]
    public async Task ResourceReads_ShouldKeepHistoricalCodesAndResolveDeletedRequesterWithoutTracking()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var state = await SeedAsync(db, 2);
        var definition = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == state.Definition.Id);
        definition.LoincCode = "9999999-9";
        definition.DisplayName = "Изменённое определение справочника";
        definition.IsActive = false;
        var worker = await db.MedicalWorkers.SingleAsync(x => x.Id == state.Worker.Id);
        worker.IsDeleted = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = scope.ServiceProvider.GetRequiredService<IFhirReadRepository>();

        var patient = (await repository.GetPatientAsync(state.Patient.Id, CancellationToken.None))!;
        patient.FirstName.Should().Be(state.Patient.FirstName);
        patient.MiddleName.Should().BeNull();
        patient.BirthDate.Should().Be(state.Patient.BirthDate);
        patient.Sex.Should().Be(AdministrativeSex.Male);
        patient.Comment.Should().BeNull();
        var requester = (await repository.GetPractitionerAsync(state.Worker.Id, CancellationToken.None))!;
        requester.IsDeleted.Should().BeTrue();
        requester.Position.Should().Be(state.Worker.Position);
        var request = (await repository.GetServiceRequestAsync(state.Requests[0].Id, CancellationToken.None))!;
        request.LoincCode.Should().Be(state.Requests[0].LoincCodeSnapshot);
        request.DisplayName.Should().Be(state.Requests[0].DisplayNameSnapshot);
        request.Status.Should().Be(LaboratoryAnalysisRequestStatus.Completed);
        var observation = (await repository.GetObservationAsync(state.Results[0].Id, CancellationToken.None))!;
        observation.LoincCode.Should().Be(request.LoincCode);
        observation.DisplayName.Should().Be(request.DisplayName);
        observation.LaboratoryAnalysisRequestId.Should().Be(request.Id);
        observation.PatientId.Should().Be(patient.Id);
        observation.Value.Should().Be("142");
        db.ChangeTracker.Entries().Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task ObservationSearch_ShouldReturnOnlyTheBasedOnResultAndRespectCursor()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var state = await SeedAsync(db, 7);
        var repository = scope.ServiceProvider.GetRequiredService<IFhirReadRepository>();
        var expected = state.Results[0];

        var page = await repository.SearchObservationsAsync(expected.LaboratoryAnalysisRequestId, 1, null,
            CancellationToken.None);

        page.Items.Should().ContainSingle().Which.Id.Should().Be(expected.Id);
        page.Next.Should().BeNull();
        var before = await repository.SearchObservationsAsync(expected.LaboratoryAnalysisRequestId, 1,
            new FhirPagePosition(expected.IssuedAtUtc.AddMilliseconds(-1), expected.Id), CancellationToken.None);
        before.Items.Should().ContainSingle().Which.Id.Should().Be(expected.Id);
        var after = await repository.SearchObservationsAsync(expected.LaboratoryAnalysisRequestId, 1,
            new FhirPagePosition(expected.IssuedAtUtc, expected.Id), CancellationToken.None);
        after.Items.Should().BeEmpty();
        after.Next.Should().BeNull();
        var unrelated = await repository.SearchObservationsAsync(state.Requests[1].Id, 20, null,
            CancellationToken.None);
        unrelated.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyDatabaseAndUnknownIds_ShouldHaveEmptySearchesAndMissingResources()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var repository = scope.ServiceProvider.GetRequiredService<IFhirReadRepository>();
        var missing = Guid.NewGuid();

        (await repository.GetPatientAsync(missing, CancellationToken.None)).Should().BeNull();
        (await repository.GetPractitionerAsync(missing, CancellationToken.None)).Should().BeNull();
        (await repository.GetServiceRequestAsync(missing, CancellationToken.None)).Should().BeNull();
        (await repository.GetObservationAsync(missing, CancellationToken.None)).Should().BeNull();
        var requests = await repository.SearchServiceRequestsAsync(null, 20, null, CancellationToken.None);
        requests.Items.Should().BeEmpty();
        requests.Next.Should().BeNull();
        var observations = await repository.SearchObservationsAsync(missing, 20, null, CancellationToken.None);
        observations.Items.Should().BeEmpty();
        observations.Next.Should().BeNull();
    }

    [Fact]
    public async Task CancelledSearch_ShouldRespectTheRequestCancellation()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFhirReadRepository>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var read = () => repository.SearchServiceRequestsAsync(null, 20, null, cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<State> SeedAsync(HospitalDbContext db, int requestCount)
    {
        var worker = new MedicalWorkerDbo
        {
            Id = Guid.NewGuid(),
            FirstName = "Алексей",
            LastName = "Проверка",
            Position = "Врач",
            Role = MedicalWorkerRole.Doctor,
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        };
        var patient = new PatientDbo
        {
            Id = Guid.NewGuid(),
            FirstName = "Иван",
            LastName = "Проверка",
            BirthDate = new DateOnly(1990, 1, 1),
            Sex = (int)AdministrativeSex.Male,
            Comment = "Внутренний комментарий",
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        };
        var definition = new LaboratoryTestDefinitionDbo
        {
            Id = Guid.NewGuid(),
            LoincCode = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()) + "-0",
            DisplayName = "Сохранённое название исследования",
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        };
        var serviceName = "fhir-" + Guid.NewGuid().ToString("N");
        var service = new ServiceAccountDbo
        {
            Id = Guid.NewGuid(),
            Name = serviceName,
            NormalizedName = serviceName.ToUpperInvariant(),
            ApiKeyHash = RandomNumberGenerator.GetBytes(32),
            CreatedAtUtc = At,
            UpdatedAtUtc = At
        };
        db.MedicalWorkers.Add(worker);
        db.Patients.Add(patient);
        db.LaboratoryTestDefinitions.Add(definition);
        db.ServiceAccounts.Add(service);
        await db.SaveChangesAsync();
        var record = new MedicalRecordDbo { Id = Guid.NewGuid(), PatientId = patient.Id, CreatedAtUtc = At, Version = 1 };
        db.MedicalRecords.Add(record);
        await db.SaveChangesAsync();
        var requests = new List<LaboratoryAnalysisRequestDbo>();
        var results = new List<LaboratoryAnalysisResultDbo>();
        for (var index = requestCount - 1; index >= 0; index--)
        {
            var request = new LaboratoryAnalysisRequestDbo
            {
                Id = Guid.Parse($"a5000000-0000-4000-8000-{index + 1:D12}"),
                MedicalRecordId = record.Id,
                PatientId = patient.Id,
                RequestedByMedicalWorkerId = worker.Id,
                LaboratoryTestDefinitionId = definition.Id,
                LoincCodeSnapshot = definition.LoincCode,
                DisplayNameSnapshot = definition.DisplayName,
                Comment = index == 0 ? "Исходный комментарий" : null,
                AuthoredAtUtc = At.AddMilliseconds(index / 5).AddTicks((4 - index % 5) * 10),
                Status = index % 3 == 0 ? LaboratoryAnalysisRequestStatus.Completed : LaboratoryAnalysisRequestStatus.Active
            };
            requests.Add(request);
            if (request.Status == LaboratoryAnalysisRequestStatus.Completed)
            {
                var result = new LaboratoryAnalysisResultDbo
                {
                    Id = Guid.NewGuid(),
                    LaboratoryAnalysisRequestId = request.Id,
                    PatientId = patient.Id,
                    Value = "142",
                    IssuedAtUtc = At.AddMinutes(index + 1),
                    ReceivedAtUtc = At.AddMinutes(index + 2),
                    CreatedByServiceAccountId = service.Id
                };
                request.Result = result;
                results.Add(result);
            }
        }
        db.LaboratoryAnalysisRequests.AddRange(requests);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new State(patient, worker, definition, requests.OrderBy(x => x.Id).ToArray(),
            results.OrderBy(x => x.LaboratoryAnalysisRequestId).ToArray());
    }

    private static DateTimeOffset Milliseconds(DateTimeOffset at) =>
        at.AddTicks(-(at.UtcTicks % TimeSpan.TicksPerMillisecond));

    private static IEnumerable<JsonElement> PlanNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in PlanNodes(child))
                yield return descendant;
    }

    private sealed class SearchCommandProbe : DbCommandInterceptor
    {
        public string CommandText { get; private set; } = string.Empty;
        public NpgsqlParameter[] Parameters { get; private set; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandText = command.CommandText;
            Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(x => x.Clone()).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed record State(PatientDbo Patient, MedicalWorkerDbo Worker, LaboratoryTestDefinitionDbo Definition,
        IReadOnlyList<LaboratoryAnalysisRequestDbo> Requests, IReadOnlyList<LaboratoryAnalysisResultDbo> Results);
}
