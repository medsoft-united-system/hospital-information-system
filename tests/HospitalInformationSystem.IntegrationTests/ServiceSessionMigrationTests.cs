using System.Security.Cryptography;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceSessionMigrationTests
{
    private const string LegacyMigration = "20261109192554_AddServiceAccountAudit";
    private const string SessionMigration = "20261110113308_AddServiceRefreshSessions";

    [Fact]
    public async Task UpgradeAndDowngrade_ShouldPreserveExistingAccountAndRefuseDroppingSavedSessions()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("hospital_service_session_upgrade").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(LegacyMigration);
        var at = new DateTimeOffset(2026, 11, 10, 10, 11, 12, TimeSpan.Zero);
        var hash = SHA256.HashData("existing-l6-service-key"u8);
        var account = new ServiceAccountDbo
        {
            Id = Guid.NewGuid(), Name = "lis.before-sessions", NormalizedName = "LIS.BEFORE-SESSIONS",
            ApiKeyHash = hash, AuthenticationVersion = 7, CreatedAtUtc = at, UpdatedAtUtc = at
        };
        db.ServiceAccounts.Add(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(SessionMigration);

        await AssertAccountUnchangedAsync(db, account.Id, hash, at);
        (await HasSessionTableAsync(db)).Should().BeTrue();
        db.Database.HasPendingModelChanges().Should().BeFalse();
        (await db.ServiceRefreshSessions.CountAsync()).Should().Be(0);

        await migrator.MigrateAsync(LegacyMigration);

        (await HasSessionTableAsync(db)).Should().BeFalse();
        (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(SessionMigration);
        await AssertAccountUnchangedAsync(db, account.Id, hash, at);

        await migrator.MigrateAsync(SessionMigration);
        var session = new ServiceRefreshSessionDbo
        {
            Id = Guid.NewGuid(), ServiceAccountId = account.Id, FamilyId = Guid.NewGuid(),
            TokenHash = Convert.ToHexStringLower(SHA256.HashData("migration-test-refresh"u8)),
            AuthenticationVersion = 7, CreatedAtUtc = at.AddMinutes(1), ExpiresAtUtc = at.AddDays(7)
        };
        db.ServiceRefreshSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var downgrade = () => migrator.MigrateAsync(LegacyMigration);

        var rejected = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
        rejected.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        rejected.MessageText.Should().Contain("таблица содержит сохранённые сессии");
        (await HasSessionTableAsync(db)).Should().BeTrue();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(SessionMigration);
        await AssertAccountUnchangedAsync(db, account.Id, hash, at);
        var restored = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync();
        restored.Id.Should().Be(session.Id);
        restored.ServiceAccountId.Should().Be(account.Id);
        restored.FamilyId.Should().Be(session.FamilyId);
        restored.TokenHash.Should().Be(session.TokenHash);
        restored.AuthenticationVersion.Should().Be(7);
        restored.CreatedAtUtc.Should().Be(at.AddMinutes(1));
        restored.ExpiresAtUtc.Should().Be(at.AddDays(7));
        restored.RevokedAtUtc.Should().BeNull();
        restored.ConsumedAtUtc.Should().BeNull();
    }

    private static Task<bool> HasSessionTableAsync(HospitalDbContext db) => db.Database.SqlQueryRaw<bool>("""
        SELECT to_regclass('service_refresh_sessions') IS NOT NULL AS "Value"
        """).SingleAsync();

    private static async Task AssertAccountUnchangedAsync(HospitalDbContext db, Guid id, byte[] hash,
        DateTimeOffset at)
    {
        var account = await db.ServiceAccounts.AsNoTracking().SingleAsync(item => item.Id == id);
        account.Name.Should().Be("lis.before-sessions");
        account.NormalizedName.Should().Be("LIS.BEFORE-SESSIONS");
        account.ApiKeyHash.Should().Equal(hash);
        account.AuthenticationVersion.Should().Be(7);
        account.CreatedAtUtc.Should().Be(at);
        account.UpdatedAtUtc.Should().Be(at);
        account.IsActive.Should().BeTrue();
    }
}
