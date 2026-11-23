using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Application.ServiceAccounts.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceSessionPersistenceTests(HospitalApiFactory factory) : IClassFixture<HospitalApiFactory>
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConsumedRefresh_ShouldKeepLogicalSessionUnrevokedUntilExplicitAccountRevocation()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = Account();
        db.ServiceAccounts.Add(account);
        var previous = Session(account.Id);
        var replacement = Session(account.Id, previous.FamilyId);
        previous.ConsumedAtUtc = At;
        previous.ReplacedBySessionId = replacement.Id;
        db.ServiceRefreshSessions.AddRange(previous, replacement);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.ServiceRefreshSessions.SingleAsync(row => row.Id == previous.Id);

        stored.ConsumedAtUtc.Should().Be(At);
        stored.ReplacedBySessionId.Should().Be(replacement.Id);
        stored.RevokedAtUtc.Should().BeNull();
        stored.RevokeReason.Should().BeNull();
        (await db.RefreshSessions.AnyAsync(row => row.Id == previous.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAll_ShouldCountActiveFamiliesAndPersistAllRowsWithoutTouchingAnotherAccount()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = Account();
        var other = Account();
        db.ServiceAccounts.AddRange(account, other);
        var old = Session(account.Id);
        var next = Session(account.Id, old.FamilyId);
        old.ConsumedAtUtc = At;
        old.ReplacedBySessionId = next.Id;
        var separate = Session(account.Id);
        var expired = Session(account.Id);
        expired.ExpiresAtUtc = At.AddMinutes(-1);
        var alreadyRevoked = Session(account.Id);
        alreadyRevoked.RevokedAtUtc = At.AddMinutes(-5);
        alreadyRevoked.RevokeReason = "previous-action";
        var untouched = Session(other.Id);
        db.ServiceRefreshSessions.AddRange(old, next, separate, expired, alreadyRevoked, untouched);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var count = await new ServiceSessionRevoker(db).RevokeAllAsync(account.Id, At,
                "service-key-rotated", CancellationToken.None);
            count.Should().Be(2);
            await transaction.CommitAsync();
        }

        var rows = await db.ServiceRefreshSessions.AsNoTracking().Where(row => row.ServiceAccountId == account.Id)
            .ToArrayAsync();
        rows.Where(row => row.Id != alreadyRevoked.Id).Should().OnlyContain(row =>
            row.RevokedAtUtc == At && row.RevokeReason == "service-key-rotated");
        rows.Single(row => row.Id == alreadyRevoked.Id).RevokeReason.Should().Be("previous-action");
        (await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(row => row.Id == untouched.Id))
            .RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task RevokeAll_RequiresCallerTransactionAndRollsBackWithIt()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = Account();
        var session = Session(account.Id);
        db.ServiceAccounts.Add(account);
        db.ServiceRefreshSessions.Add(session);
        await db.SaveChangesAsync();
        var revoker = new ServiceSessionRevoker(db);
        var withoutTransaction = () => revoker.RevokeAllAsync(account.Id, At,
            "service-account-deactivated", CancellationToken.None);
        await withoutTransaction.Should().ThrowAsync<InvalidOperationException>();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await revoker.RevokeAllAsync(account.Id, At, "service-account-deactivated", CancellationToken.None))
                .Should().Be(1);
            await transaction.RollbackAsync();
        }

        (await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(row => row.Id == session.Id))
            .RevokedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("other-account")]
    [InlineData("other-family")]
    [InlineData("self")]
    [InlineData("missing-replacement")]
    [InlineData("replacement-without-consumption")]
    public async Task Replacement_ShouldNotCrossServiceOrFamilyOrCreateInvalidConsumedState(string invalid)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = Account();
        var other = Account();
        db.ServiceAccounts.AddRange(account, other);
        var first = Session(account.Id);
        var next = Session(invalid == "other-account" ? other.Id : account.Id,
            invalid == "other-family" ? Guid.NewGuid() : first.FamilyId);
        db.ServiceRefreshSessions.AddRange(first, next);
        await db.SaveChangesAsync();

        first.ConsumedAtUtc = invalid == "replacement-without-consumption" ? null : At;
        first.ReplacedBySessionId = invalid switch
        {
            "missing-replacement" => null,
            "self" => first.Id,
            _ => next.Id
        };
        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<BusinessConflictException>();
        db.ChangeTracker.Clear();
        (await db.ServiceRefreshSessions.SingleAsync(row => row.Id == first.Id)).ConsumedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ClockMovingBack_ShouldNotPreventImmediateRevocation()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = Account();
        var session = Session(account.Id);
        session.CreatedAtUtc = At.AddSeconds(1);
        db.ServiceAccounts.Add(account);
        db.ServiceRefreshSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync();

        (await new ServiceSessionRevoker(db).RevokeAllAsync(account.Id, At, "service-key-rotated",
            CancellationToken.None)).Should().Be(1);
        await transaction.CommitAsync();

        var saved = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(row => row.Id == session.Id);
        saved.RevokedAtUtc.Should().Be(session.CreatedAtUtc);
        saved.RevokeReason.Should().Be("service-key-rotated");
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("dates")]
    [InlineData("family")]
    [InlineData("revoke-reason")]
    public async Task InvalidCredentialRows_ShouldFailAtDatabaseBoundary(string invalid)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var account = Account();
        var session = Session(account.Id);
        switch (invalid)
        {
            case "hash": session.TokenHash = "not-a-hash"; break;
            case "version": session.AuthenticationVersion = 0; break;
            case "dates": session.ExpiresAtUtc = session.CreatedAtUtc; break;
            case "family": session.FamilyId = Guid.Empty; break;
            case "revoke-reason": session.RevokedAtUtc = At; break;
        }
        db.ServiceAccounts.Add(account);
        db.ServiceRefreshSessions.Add(session);
        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<BusinessConflictException>();
    }

    private static ServiceAccountDbo Account()
    {
        var account = ServiceAccountFactory.Create("lis-" + Guid.NewGuid().ToString("N"),
            RandomNumberGenerator.GetBytes(32), At.AddDays(-1));
        return new ServiceAccountDbo
        {
            Id = account.Id, Name = account.Name, NormalizedName = account.NormalizedName,
            ApiKeyHash = account.ApiKeyHash, AuthenticationVersion = 1,
            CreatedAtUtc = account.CreatedAtUtc, UpdatedAtUtc = account.UpdatedAtUtc
        };
    }

    private static ServiceRefreshSessionDbo Session(Guid accountId, Guid? familyId = null) => new()
    {
        Id = Guid.NewGuid(), ServiceAccountId = accountId, FamilyId = familyId ?? Guid.NewGuid(),
        TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))),
        AuthenticationVersion = 1, CreatedAtUtc = At.AddHours(-1), ExpiresAtUtc = At.AddDays(7)
    };
}
