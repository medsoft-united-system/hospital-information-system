using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Auth.Services;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Application.DI;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.DI;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AdministratorSetupServiceTests : IAsyncLifetime
{
    private const string OwnerToken = "integration-owner-token-256bits-minimum-test-value";
    private const string Password = "Administrator.New-Password.827!";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly TestTimeProvider _clock = new();
    private readonly TotpCodeService _totp = new();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _clock.SetUtcNow(new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddApplication();
        services.AddInfrastructure(_postgres.GetConnectionString());
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<TotpCodeService>();
        services.AddScoped<IAdministratorSetupService, AdministratorSetupService>();
        services.AddScoped<IAdministratorSetupCryptography,
            AdministratorSetupCryptography>();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AdministratorSetupOptions>>().Value);
        services.Configure<AdministratorSetupOptions>(options =>
            options.TokenSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OwnerToken))));
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.MigrateAsync(CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>().InitializeRolesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Confirm_ValidCode_ShouldCreateAdministratorOnlyAfterProofAndProtectSecret()
    {
        var challenge = await BeginAsync();
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Users.CountAsync(CancellationToken.None)).Should().Be(0);
            var stored = await db.Set<AdministratorSetupChallengeDbo>().SingleAsync(CancellationToken.None);
            stored.PasswordHash.Should().StartWith("$argon2id$").And.NotContain(Password);
            stored.TotpSecretProtected.Should().NotBe(challenge.SecretKey);
        }

        await ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));

        await using var verification = _services.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var administrator = await database.Users.SingleAsync(CancellationToken.None);
        administrator.IsAdministrator.Should().BeTrue();
        administrator.TwoFactorEnabled.Should().BeTrue();
        var users = verification.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
        (await users.CheckPasswordAsync(administrator, Password)).Should().BeTrue();
        var state = await database.AdministratorSetupStates.SingleAsync(CancellationToken.None);
        state.IsCompleted.Should().BeTrue();
        state.TotpSecretProtected.Should().NotContain(challenge.SecretKey);
        (await database.Set<AdministratorSetupChallengeDbo>().AnyAsync(CancellationToken.None)).Should().BeFalse();
        (await verification.ServiceProvider.GetRequiredService<IAdministratorSetupService>()
            .GetStatusAsync(CancellationToken.None)).SetupRequired.Should().BeFalse();
        var audits = await database.AuditEvents.ToListAsync(CancellationToken.None);
        audits.Should().ContainSingle(entry => entry.EventType == nameof(AuditEventType.AdministratorSetup));
        audits.Should().OnlyContain(entry => !entry.MetadataJson.Contains(Password) &&
            !entry.MetadataJson.Contains(challenge.SecretKey) && !entry.Message.Contains(Password));
    }

    [Fact]
    public async Task Begin_InvalidOwnershipOrConfirmation_ShouldLeaveDatabaseUnconfigured()
    {
        await using var scope = _services.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<IAdministratorSetupService>();
        var ownerFailure = () => setup.BeginAsync(new BeginSetupRequest(Password, Password),
            "incorrect-owner-token-long-enough-for-format", CancellationToken.None);
        var passwordFailure = () => setup.BeginAsync(new BeginSetupRequest(Password, "different"), OwnerToken,
            CancellationToken.None);

        await ownerFailure.Should().ThrowAsync<SetupAccessDeniedException>();
        await passwordFailure.Should().ThrowAsync<DomainValidationException>();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Users.AnyAsync(CancellationToken.None)).Should().BeFalse();
        (await db.Set<AdministratorSetupChallengeDbo>().AnyAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_WrongCodeAtAttemptLimit_ShouldPersistFailureAndPreventCompletion()
    {
        _services.GetRequiredService<IOptions<AdministratorSetupOptions>>().Value.MaxChallengeAttempts = 1;
        var challenge = await BeginAsync();
        var wrongCode = () => ConfirmAsync(challenge, "invalid");

        await wrongCode.Should().ThrowAsync<DomainValidationException>();
        var correctAfterLimit = () => ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));
        await correctAfterLimit.Should().ThrowAsync<DomainValidationException>();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Set<AdministratorSetupChallengeDbo>().SingleAsync(CancellationToken.None)).FailedAttempts.Should().Be(1);
        (await db.Users.AnyAsync(CancellationToken.None)).Should().BeFalse();
        (await db.AuditEvents.CountAsync(entry => entry.EventType == nameof(AuditEventType.LoginFailed),
            CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task Confirm_ExpiredChallenge_ShouldRequireRestartingSetup()
    {
        var challenge = await BeginAsync();
        _clock.SetUtcNow(challenge.ExpiresAt.AddSeconds(1));
        var confirm = () => ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));

        await confirm.Should().ThrowAsync<DomainValidationException>();
        await using var scope = _services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Users.AnyAsync(CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_ConcurrentRequests_ShouldCompleteOneAdministratorExactlyOnce()
    {
        var challenge = await BeginAsync();
        var code = _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow());

        var attempts = await Task.WhenAll(TryConfirmAsync(), TryConfirmAsync());

        attempts.Should().ContainSingle(success => success);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Users.CountAsync(CancellationToken.None)).Should().Be(1);
        (await db.AdministratorSetupStates.CountAsync(state => state.IsCompleted, CancellationToken.None)).Should().Be(1);
        (await db.AuditEvents.CountAsync(entry => entry.EventType == nameof(AuditEventType.AdministratorSetup),
            CancellationToken.None)).Should().Be(1);

        async Task<bool> TryConfirmAsync()
        {
            try { await ConfirmAsync(challenge, code); return true; }
            catch (BusinessConflictException) { return false; }
        }
    }

    [Fact]
    public async Task Begin_CompletedSetupInNewServiceScope_ShouldRemainClosed()
    {
        var challenge = await BeginAsync();
        await ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));

        var restart = () => BeginAsync();

        await restart.Should().ThrowAsync<BusinessConflictException>();
    }

    [Fact]
    public async Task Verify_ReplayedCodeAndAttemptLockout_ShouldRequireNewStepAndWaitForUnlock()
    {
        _services.GetRequiredService<IOptions<AdministratorSetupOptions>>().Value.MaxTotpAttempts = 2;
        var challenge = await BeginAsync();
        await ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));
        var userId = await AdministratorIdAsync();
        _clock.SetUtcNow(_clock.GetUtcNow().AddSeconds(30));
        var code = _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow());

        (await VerifyAsync(userId, code)).Should().BeTrue();
        (await VerifyAsync(userId, code)).Should().BeFalse();
        (await VerifyAsync(userId, "invalid")).Should().BeFalse();
        _clock.SetUtcNow(_clock.GetUtcNow().AddSeconds(30));
        (await VerifyAsync(userId, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()))).Should().BeFalse();
        _clock.SetUtcNow(_clock.GetUtcNow().AddMinutes(5));
        (await VerifyAsync(userId, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()))).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_ConcurrentCodeReuse_ShouldAllowOneLoginOnly()
    {
        var challenge = await BeginAsync();
        await ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));
        var userId = await AdministratorIdAsync();
        _clock.SetUtcNow(_clock.GetUtcNow().AddSeconds(30));
        var code = _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow());

        var results = await Task.WhenAll(VerifyAsync(userId, code), VerifyAsync(userId, code));

        results.Should().ContainSingle(accepted => accepted);
    }

    [Fact]
    public async Task Confirm_ExistingAdministrator_ShouldPreserveIdentityAndRevokeExistingSessions()
    {
        Guid administratorId;
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>()
                .InitializeAsync("legacy.admin", "Legacy.Password-491!", CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            administratorId = (await db.Users.SingleAsync(CancellationToken.None)).Id;
            db.RefreshSessions.Add(new RefreshSessionDbo
            {
                Id = Guid.NewGuid(),
                FamilyId = Guid.NewGuid(),
                UserId = administratorId,
                TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                CreatedAtUtc = _clock.GetUtcNow(),
                ExpiresAtUtc = _clock.GetUtcNow().AddDays(1)
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        var challenge = await BeginAsync();

        await ConfirmAsync(challenge, _totp.GetCode(challenge.SecretKey, _clock.GetUtcNow()));

        await using var verification = _services.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var administrator = await database.Users.SingleAsync(CancellationToken.None);
        administrator.Id.Should().Be(administratorId);
        administrator.UserName.Should().Be("legacy.admin");
        var users = verification.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
        (await users.CheckPasswordAsync(administrator, "Legacy.Password-491!")).Should().BeFalse();
        (await users.CheckPasswordAsync(administrator, Password)).Should().BeTrue();
        (await database.RefreshSessions.SingleAsync(CancellationToken.None)).RevokeReason.Should().Be("administrator-setup");
        var audit = await database.AuditEvents.SingleAsync(entry => entry.EventType == nameof(AuditEventType.AdministratorSetup),
            CancellationToken.None);
        JsonSerializer.Deserialize<JsonElement>(audit.MetadataJson).GetProperty("revokedRefreshTokenCount")
            .GetInt32().Should().Be(1);
    }

    private async Task<SetupChallengeDto> BeginAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdministratorSetupService>()
            .BeginAsync(new BeginSetupRequest(Password, Password), OwnerToken, CancellationToken.None);
    }

    private async Task ConfirmAsync(SetupChallengeDto challenge, string code)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAdministratorSetupService>()
            .ConfirmAsync(new ConfirmSetupRequest(challenge.ChallengeId, code), OwnerToken, CancellationToken.None);
    }

    private async Task<bool> VerifyAsync(Guid userId, string code)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdministratorSetupService>()
            .VerifyAdministratorCodeAsync(userId, code, CancellationToken.None);
    }

    private async Task<Guid> AdministratorIdAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Users.SingleAsync(CancellationToken.None)).Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
