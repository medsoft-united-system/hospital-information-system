using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceApiKeyHashMigrationTests
{
    private const string LegacyMigration = "20261111160000_IndexFhirServiceRequestPages";

    [Fact]
    public async Task UpgradeAndDowngrade_ShouldPreserveLegacyKeysAndRefuseLosingArgon2idHashes()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("hospital_service_api_key_upgrade").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        var argonMigration = db.Database.GetMigrations().Single(name =>
            name.EndsWith("_UseArgon2idForServiceApiKeys", StringComparison.Ordinal));
        await migrator.MigrateAsync(LegacyMigration);
        var at = new DateTimeOffset(2026, 11, 13, 12, 34, 56, TimeSpan.Zero);
        var legacy = Account("lis.before-argon", SHA256.HashData("existing-service-api-key"u8), at);
        db.ServiceAccounts.Add(legacy);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(argonMigration);

        await AssertUnchangedAsync(db, legacy);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var argon = Account("lis.after-argon", [], at);
        var credential = new ServiceApiKeyGenerator().Generate(argon.Id);
        argon.ApiKeyHash = credential.Hash;
        db.ServiceAccounts.Add(argon);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var savedArgon = await db.ServiceAccounts.AsNoTracking().SingleAsync(row => row.Id == argon.Id);
        ServiceAccountsApiTests.AssertArgonKey(credential.ApiKey, argon.Id, savedArgon.ApiKeyHash);

        foreach (var malformed in new[]
        {
            new byte[31],
            new byte[96],
            Encoding.UTF8.GetBytes("$argon2id$v=16$" + new string('a', 82)),
            Encoding.UTF8.GetBytes("$argon2id$v=19$" + new string('a', 115))
        })
        {
            var invalidWrite = () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE service_accounts SET api_key_hash = {malformed} WHERE id = {legacy.Id}");
            (await invalidWrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should()
                .Be(PostgresErrorCodes.CheckViolation);
        }
        await AssertUnchangedAsync(db, legacy);
        var appliedBefore = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

        var downgrade = () => migrator.MigrateAsync(LegacyMigration);

        (await downgrade.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should()
            .Be(PostgresErrorCodes.RaiseException);
        (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(appliedBefore);
        await AssertUnchangedAsync(db, legacy);
        await AssertUnchangedAsync(db, argon);
        (await db.ServiceRefreshSessions.CountAsync()).Should().Be(0);

        await db.ServiceAccounts.Where(row => row.Id == argon.Id).ExecuteDeleteAsync();
        await migrator.MigrateAsync(LegacyMigration);

        (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(argonMigration);
        await AssertUnchangedAsync(db, legacy);
        (await db.ServiceAccounts.CountAsync()).Should().Be(1);
        var rejectedArgonWrite = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_accounts SET api_key_hash = {credential.Hash} WHERE id = {legacy.Id}");
        (await rejectedArgonWrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should()
            .Be(PostgresErrorCodes.CheckViolation);
        await AssertUnchangedAsync(db, legacy);
    }

    private static ServiceAccountDbo Account(string name, byte[] hash, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant(),
        ApiKeyHash = hash, AuthenticationVersion = 7, CreatedAtUtc = at, UpdatedAtUtc = at
    };

    private static async Task AssertUnchangedAsync(HospitalDbContext db, ServiceAccountDbo expected)
    {
        var account = await db.ServiceAccounts.AsNoTracking().SingleAsync(row => row.Id == expected.Id);
        account.Should().BeEquivalentTo(expected);
    }
}
