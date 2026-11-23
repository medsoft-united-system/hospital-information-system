using FluentAssertions;
using HospitalInformationSystem.Application.ServiceAccounts.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAccountPersistenceTests(HospitalApiFactory factory) : IClassFixture<HospitalApiFactory>
{
    [Fact]
    public async Task Save_ShouldKeepServiceAccountSeparateFromHumanUsers()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = NewAccount();
        db.ServiceAccounts.Add(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.ServiceAccounts.SingleAsync(x => x.Id == account.Id);
        saved.ApiKeyHash.Should().Equal(account.ApiKeyHash);
        saved.AuthenticationVersion.Should().Be(1);
        saved.IsActive.Should().BeTrue();
        saved.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        db.Entry(saved).Property<uint>("xmin").CurrentValue.Should().BeGreaterThan(0);
        (await db.Users.AnyAsync(x => x.Id == account.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Save_DuplicateNormalizedName_ShouldConflict()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var name = "lis-" + Guid.NewGuid().ToString("N");
        db.ServiceAccounts.Add(NewAccount(name));
        await db.SaveChangesAsync();
        db.ServiceAccounts.Add(NewAccount(name.ToUpperInvariant()));
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<BusinessConflictException>();
    }

    [Theory]
    [InlineData("name", "ab")]
    [InlineData("normalized_name", " ")]
    [InlineData("role", "doctor")]
    public async Task Update_InvalidText_ShouldBeRejectedByDatabase(string column, string value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = NewAccount();
        db.ServiceAccounts.Add(account);
        await db.SaveChangesAsync();
        var sql = column switch
        {
            "name" => "UPDATE service_accounts SET name = @value WHERE id = @id",
            "normalized_name" => "UPDATE service_accounts SET normalized_name = @value WHERE id = @id",
            "role" => "UPDATE service_accounts SET role = @value WHERE id = @id",
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
        var update = () => db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("value", value), new NpgsqlParameter("id", account.Id));
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should()
            .Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Update_InvalidHashVersionOrDates_ShouldBeRejectedByDatabase()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = NewAccount();
        db.ServiceAccounts.Add(account);
        await db.SaveChangesAsync();

        var badHash = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE service_accounts SET api_key_hash = {new byte[31]} WHERE id = {account.Id}");
        (await badHash.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        var badVersion = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE service_accounts SET authentication_version = 0 WHERE id = {account.Id}");
        (await badVersion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        var badDates = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE service_accounts SET updated_at_utc = created_at_utc - INTERVAL '1 second' WHERE id = {account.Id}");
        (await badDates.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Save_StaleServiceAccount_ShouldNotOverwriteConcurrentChange()
    {
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var first = NewAccount();
        firstDb.ServiceAccounts.Add(first);
        await firstDb.SaveChangesAsync();
        var stale = await secondDb.ServiceAccounts.SingleAsync(x => x.Id == first.Id);

        first.IsActive = false;
        first.AuthenticationVersion++;
        await firstDb.SaveChangesAsync();
        stale.Name = "Changed " + stale.Name;
        stale.NormalizedName = stale.Name.ToUpperInvariant();
        var saveStale = () => secondDb.SaveChangesAsync();
        await saveStale.Should().ThrowAsync<BusinessConflictException>();
    }

    private static ServiceAccountDbo NewAccount(string? name = null)
    {
        var account = ServiceAccountFactory.Create(name ?? "lis-" + Guid.NewGuid().ToString("N"),
            System.Security.Cryptography.SHA256.HashData("test-service-key"u8), DateTimeOffset.UtcNow);
        return new ServiceAccountDbo
        {
            Id = account.Id, Name = account.Name, NormalizedName = account.NormalizedName,
            Role = account.Role, IsActive = account.IsActive, ApiKeyHash = account.ApiKeyHash,
            AuthenticationVersion = account.AuthenticationVersion,
            CreatedAtUtc = account.CreatedAtUtc, UpdatedAtUtc = account.UpdatedAtUtc
        };
    }
}
