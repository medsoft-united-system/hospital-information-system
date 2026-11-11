using FluentAssertions;
using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirReadPaginationMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyMicrosecondRequests_ShouldBackfillStableWireKeysAndPreserveSourceTimes()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new HospitalDbContext(options);
        var migrator = db.GetService<IMigrator>();
        const string previousMigration = "20261109121005_AddLaboratoryAnalyses";
        await migrator.MigrateAsync(previousMigration);
        var workerId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO medical_workers
                (id, first_name, last_name, position, role, is_deleted, created_at_utc, updated_at_utc)
            VALUES ({workerId}, 'Алексей', 'Проверка', 'Врач', 'doctor', false, {at}, {at});
            INSERT INTO patients
                (id, first_name, last_name, birth_date, sex, created_at_utc, updated_at_utc)
            VALUES ({patientId}, 'Иван', 'Проверка', DATE '1990-01-01', 1, {at}, {at});
            INSERT INTO medical_records (id, patient_id, created_at_utc, version)
            VALUES ({recordId}, {patientId}, {at}, 1);
            INSERT INTO laboratory_test_definitions
                (id, loinc_code, display_name, is_active, created_at_utc, updated_at_utc)
            VALUES ({definitionId}, '2345-7', 'Глюкоза', true, {at}, {at});
            """);
        var requests = new[]
        {
            new LegacyRequestRow { Id = Guid.Parse("a5000000-0000-4000-8000-000000000001"), AuthoredAtUtc = at.AddTicks(9000) },
            new LegacyRequestRow { Id = Guid.Parse("a5000000-0000-4000-8000-000000000002"), AuthoredAtUtc = at.AddTicks(1000) },
            new LegacyRequestRow { Id = Guid.Parse("a5000000-0000-4000-8000-000000000003"), AuthoredAtUtc = at.AddTicks(11000) }
        };
        foreach (var request in requests)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO laboratory_analysis_requests
                    (id, medical_record_id, patient_id, requested_by_medical_worker_id, laboratory_test_definition_id,
                     loinc_code_snapshot, display_name_snapshot, authored_at_utc, status)
                VALUES ({request.Id}, {recordId}, {patientId}, {workerId}, {definitionId},
                        '2345-7', 'Глюкоза', {request.AuthoredAtUtc}, 'active')
                """);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Asia/Kathmandu'");

        await migrator.MigrateAsync();

        var stored = await db.LaboratoryAnalysisRequests.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.AuthoredAtUtc, WireAtUtc = EF.Property<DateTimeOffset>(x, "AuthoredAtFhirUtc")
            }).ToArrayAsync();
        stored.Select(x => x.AuthoredAtUtc).Should().Equal(requests.Select(x => x.AuthoredAtUtc));
        stored.Select(x => x.WireAtUtc).Should().Equal(at, at, at.AddMilliseconds(1));
        var repository = new FhirReadRepository(db);
        var first = await repository.SearchServiceRequestsAsync(null, 1, null, CancellationToken.None);
        var second = await repository.SearchServiceRequestsAsync(null, 1, first.Next, CancellationToken.None);
        var third = await repository.SearchServiceRequestsAsync(null, 1, second.Next, CancellationToken.None);
        new[] { first.Items.Single().Id, second.Items.Single().Id, third.Items.Single().Id }
            .Should().Equal(requests.Select(x => x.Id));
        first.Next.Should().Be(new FhirPagePosition(at, requests[0].Id));
        second.Next.Should().Be(new FhirPagePosition(at, requests[1].Id));
        third.Next.Should().BeNull();
        var changedAt = requests[0].AuthoredAtUtc.AddMilliseconds(5);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE laboratory_analysis_requests SET authored_at_utc = {changedAt} WHERE id = {requests[0].Id}
            """);
        var changed = await db.LaboratoryAnalysisRequests.AsNoTracking().Where(x => x.Id == requests[0].Id)
            .Select(x => EF.Property<DateTimeOffset>(x, "AuthoredAtFhirUtc")).SingleAsync();
        changed.Should().Be(at.AddMilliseconds(5));

        await migrator.MigrateAsync(previousMigration);
        var downgraded = await db.Database.SqlQuery<LegacyRequestRow>($"""
            SELECT id AS "Id", authored_at_utc AS "AuthoredAtUtc" FROM laboratory_analysis_requests ORDER BY id
            """).ToArrayAsync();
        downgraded.Select(x => x.AuthoredAtUtc)
            .Should().Equal(changedAt, requests[1].AuthoredAtUtc, requests[2].AuthoredAtUtc);
        await migrator.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private sealed class LegacyRequestRow
    {
        public Guid Id { get; set; }
        public DateTimeOffset AuthoredAtUtc { get; set; }
    }
}
