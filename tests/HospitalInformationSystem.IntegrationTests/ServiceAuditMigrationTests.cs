using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAuditMigrationTests
{
    private const string LegacyMigration = "20261109121005_AddLaboratoryAnalyses";
    private const string AuditMigration = "20261109192554_AddServiceAccountAudit";

    [Fact]
    public async Task UpgradeAndDowngrade_ShouldPreserveLegacyAuditAndRefuseLosingServiceIdentity()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("hospital_service_audit_upgrade").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(LegacyMigration);
        var userEventId = Guid.NewGuid();
        var systemEventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 9, 12, 23, 45, TimeSpan.Zero);
        const string userMetadata = """{"action":"legacy-user","targetId":"12345678-1234-4234-8234-123456789abc"}""";
        const string systemMetadata = """{"action":"legacy-system"}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO security_audit_events (event_id, event_time_utc, event_type, actor_kind, actor_user_id,
                actor_display_name, request_id, component, message, metadata)
            VALUES ({userEventId}, {at}, 'MedicalRecordChanged', 'user', {userId}, 'legacy.admin',
                'legacy-request', NULL, 'Историческое событие пользователя.', {userMetadata}::jsonb);
            INSERT INTO security_audit_events (event_id, event_time_utc, event_type, actor_kind, actor_user_id,
                actor_display_name, request_id, component, message, metadata)
            VALUES ({systemEventId}, {at}, 'AdministratorSetup', 'system', NULL, 'Legacy system',
                NULL, 'LegacyComponent', 'Историческое событие системы.', {systemMetadata}::jsonb);
            """);

        await migrator.MigrateAsync(AuditMigration);

        await AssertLegacyEventsAsync(db, userEventId, systemEventId, userId, at);
        (await HasServiceColumnAsync(db)).Should().BeTrue();
        db.Database.HasPendingModelChanges().Should().BeFalse();

        await migrator.MigrateAsync(LegacyMigration);

        (await HasServiceColumnAsync(db)).Should().BeFalse();
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM security_audit_events")
            .SingleAsync()).Should().Be(2);
        (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(AuditMigration);

        await migrator.MigrateAsync(AuditMigration);
        await AssertLegacyEventsAsync(db, userEventId, systemEventId, userId, at);
        var service = new ServiceAccountDbo
        {
            Id = Guid.NewGuid(), Name = "lis.migration", NormalizedName = "LIS.MIGRATION",
            ApiKeyHash = SHA256.HashData("migration-test-key"u8), CreatedAtUtc = at, UpdatedAtUtc = at
        };
        db.ServiceAccounts.Add(service);
        var serviceEvent = new AuditEventFactory(TimeProvider.System).Create(AuditEventType.MedicalRecordChanged,
            "Сервис зарегистрировал результат анализа.",
            new AuditActor(AuditActorKind.ServiceAccount, null, service.Name, "service-request", null, service.Id));
        db.AuditEvents.Add(serviceEvent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var downgrade = () => migrator.MigrateAsync(LegacyMigration);

        var rejected = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
        rejected.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        rejected.MessageText.Should().Contain("журнал содержит события сервисных аккаунтов");
        (await HasServiceColumnAsync(db)).Should().BeTrue();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(AuditMigration);
        await AssertLegacyEventsAsync(db, userEventId, systemEventId, userId, at);
        var preserved = await db.AuditEvents.AsNoTracking().SingleAsync(item => item.EventId == serviceEvent.EventId);
        preserved.ActorKind.Should().Be("service");
        preserved.ActorServiceAccountId.Should().Be(service.Id);
        preserved.ActorUserId.Should().BeNull();
        (await db.AuditEvents.CountAsync()).Should().Be(3);
    }

    private static Task<bool> HasServiceColumnAsync(HospitalDbContext db) => db.Database.SqlQueryRaw<bool>("""
        SELECT EXISTS (SELECT 1 FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'security_audit_events'
              AND column_name = 'actor_service_account_id') AS "Value"
        """).SingleAsync();

    private static async Task AssertLegacyEventsAsync(HospitalDbContext db, Guid userEventId, Guid systemEventId,
        Guid userId, DateTimeOffset at)
    {
        var events = await db.AuditEvents.AsNoTracking().Where(item =>
            item.EventId == userEventId || item.EventId == systemEventId).ToListAsync();
        events.Should().HaveCount(2).And.OnlyContain(item => item.ActorServiceAccountId == null && item.EventTimeUtc == at);
        var user = events.Single(item => item.EventId == userEventId);
        user.ActorKind.Should().Be("user");
        user.ActorUserId.Should().Be(userId);
        user.ActorDisplayName.Should().Be("legacy.admin");
        user.RequestId.Should().Be("legacy-request");
        user.Message.Should().Be("Историческое событие пользователя.");
        using var userMetadata = JsonDocument.Parse(user.MetadataJson);
        userMetadata.RootElement.GetProperty("action").GetString().Should().Be("legacy-user");
        userMetadata.RootElement.GetProperty("targetId").GetGuid().Should()
            .Be(Guid.Parse("12345678-1234-4234-8234-123456789abc"));
        var system = events.Single(item => item.EventId == systemEventId);
        system.ActorKind.Should().Be("system");
        system.ActorUserId.Should().BeNull();
        system.ActorDisplayName.Should().Be("Legacy system");
        system.Component.Should().Be("LegacyComponent");
        system.Message.Should().Be("Историческое событие системы.");
        using var systemMetadata = JsonDocument.Parse(system.MetadataJson);
        systemMetadata.RootElement.GetProperty("action").GetString().Should().Be("legacy-system");
    }
}
