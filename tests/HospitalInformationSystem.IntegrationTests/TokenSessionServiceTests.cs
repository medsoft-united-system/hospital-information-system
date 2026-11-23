using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Identity.Tokens;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class TokenSessionServiceTests(SecureHospitalApiFactory factory)
    : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task IssueSession_ActiveMedicalAccount_ShouldStoreOnlyRefreshHashAndIssueValidRs256Access()
    {
        var account = await CreateAccountAsync();
        var issued = await IssueAsync(account.Id);

        issued.RefreshToken.Should().HaveLength(43);
        var principal = Principal(issued);
        principal.IsInRole(account.AccountRole).Should().BeTrue();
        (await ValidateAsync(principal)).Should().BeTrue();
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().RefreshSessions
            .SingleAsync(item => item.UserId == account.Id);
        row.TokenHash.Should().MatchRegex("^[a-f0-9]{64}$").And.NotBe(issued.RefreshToken);
        row.ExpiresAtUtc.Should().BeCloseTo(issued.RefreshTokenExpiresAt, TimeSpan.FromMicroseconds(1));
        issued.Tokens.AccessTokenExpiresAt.Should().BeBefore(issued.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task RotateSession_ValidRefresh_ShouldReplacePairAndRejectPreviousAccessImmediately()
    {
        var account = await CreateAccountAsync();
        var initial = await IssueAsync(account.Id);

        var rotated = await RotateAsync(initial.RefreshToken);

        rotated.Should().NotBeNull();
        rotated!.RefreshToken.Should().NotBe(initial.RefreshToken);
        (await ValidateAsync(Principal(initial))).Should().BeFalse();
        (await ValidateAsync(Principal(rotated))).Should().BeTrue();
        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().RefreshSessions
            .Where(item => item.UserId == account.Id).ToArrayAsync();
        rows.Should().HaveCount(2);
        rows.Select(item => item.FamilyId).Distinct().Should().ContainSingle();
        var previous = rows.Single(item => item.RevokedAtUtc != null);
        previous.RevokeReason.Should().Be("rotated");
        previous.ReplacedBySessionId.Should().Be(rows.Single(item => item.RevokedAtUtc == null).Id);
    }

    [Fact]
    public async Task RotateSession_ReusedRefresh_ShouldRevokeWholeFamilyAndRejectReplacement()
    {
        var account = await CreateAccountAsync();
        var initial = await IssueAsync(account.Id);
        var rotated = (await RotateAsync(initial.RefreshToken))!;

        var replay = await RotateAsync(initial.RefreshToken);

        replay.Should().BeNull();
        (await ValidateAsync(Principal(rotated))).Should().BeFalse();
        (await RotateAsync(rotated.RefreshToken)).Should().BeNull();
        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().RefreshSessions
            .Where(item => item.UserId == account.Id).ToArrayAsync();
        rows.Should().OnlyContain(item => item.RevokedAtUtc != null);
        rows.Should().Contain(item => item.RevokeReason == "refresh-reuse");
    }

    [Fact]
    public async Task RotateSession_ConcurrentRequests_ShouldProduceOneContinuationAndRevokeItOnDetectedReuse()
    {
        var account = await CreateAccountAsync();
        var initial = await IssueAsync(account.Id);

        var attempts = await Task.WhenAll(RotateAsync(initial.RefreshToken), RotateAsync(initial.RefreshToken));

        attempts.Count(item => item is not null).Should().Be(1);
        var continuation = attempts.Single(item => item is not null)!;
        (await ValidateAsync(Principal(continuation))).Should().BeFalse();
        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().RefreshSessions
            .Where(item => item.UserId == account.Id).ToArrayAsync();
        rows.Should().HaveCount(2).And.OnlyContain(item => item.RevokedAtUtc != null);
    }

    [Fact]
    public async Task IssueSession_PasswordResetAfterVerification_ShouldRejectStaleAuthenticationVersionEvenWhenUserWasTracked()
    {
        var account = await CreateAccountAsync();
        await using var loginScope = factory.Services.CreateAsyncScope();
        var tracked = await loginScope.ServiceProvider.GetRequiredService<HospitalDbContext>().Users.SingleAsync(item => item.Id == account.Id);
        var version = tracked.SecurityStamp!;
        await using (var resetScope = factory.Services.CreateAsyncScope())
            await resetScope.ServiceProvider.GetRequiredService<IAccountService>()
                .ResetPasswordAsync(account.Id, CancellationToken.None);

        var issue = await Service(loginScope.ServiceProvider).IssueAsync(account.Id, version, CancellationToken.None);

        issue.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateSession_DeactivationOrStampChange_ShouldRejectExistingAccessAndRefresh(bool deactivate)
    {
        var account = await CreateAccountAsync();
        var initial = await IssueAsync(account.Id);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            if (deactivate)
                await scope.ServiceProvider.GetRequiredService<IAccountService>()
                    .SetActiveAsync(account.Id, false, CancellationToken.None);
            else
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
                var user = (await users.FindByIdAsync(account.Id.ToString()))!;
                (await users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();
            }
        }

        (await ValidateAsync(Principal(initial))).Should().BeFalse();
        (await RotateAsync(initial.RefreshToken)).Should().BeNull();
    }

    [Fact]
    public async Task LogoutSession_RefreshCookieWithoutAccess_ShouldRevokeOnlyItsOwnFamily()
    {
        var account = await CreateAccountAsync();
        var current = await IssueAsync(account.Id);
        var another = await IssueAsync(account.Id);
        await using var scope = factory.Services.CreateAsyncScope();

        var revoked = await Service(scope.ServiceProvider).RevokeFamilyAsync(current.RefreshToken, CancellationToken.None);

        revoked.Should().Be(1);
        (await ValidateAsync(Principal(current))).Should().BeFalse();
        (await ValidateAsync(Principal(another))).Should().BeTrue();
        (await RotateAsync(current.RefreshToken)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-refresh")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task RotateSession_UnknownOrMalformedRefresh_ShouldReject(string refresh)
    {
        (await RotateAsync(refresh)).Should().BeNull();
    }

    private async Task<AccountDetailsDto> CreateAccountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var worker = new MedicalWorkerDbo
        {
            Id = Guid.NewGuid(), FirstName = "Лев", LastName = "Проверочный", Position = "Врач",
            Role = Models.MedicalWorkers.MedicalWorkerRole.Doctor,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        db.MedicalWorkers.Add(worker);
        await db.SaveChangesAsync(CancellationToken.None);
        return (await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateForWorkerAsync(worker.Id,
            new CreateWorkerAccountRequest("tok." + Guid.NewGuid().ToString("N")[..12]), CancellationToken.None)).Account;
    }

    private async Task<TokenSessionResult> IssueAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Users.AsNoTracking()
            .SingleAsync(item => item.Id == userId);
        return (await Service(scope.ServiceProvider).IssueAsync(userId, user.SecurityStamp!, CancellationToken.None))!;
    }

    private async Task<TokenSessionResult?> RotateAsync(string refresh)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await Service(scope.ServiceProvider).RotateAsync(refresh, CancellationToken.None);
    }

    private async Task<bool> ValidateAsync(ClaimsPrincipal principal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await Service(scope.ServiceProvider).ValidatePrincipalAsync(principal, CancellationToken.None);
    }

    private static TokenSessionService Service(IServiceProvider services) =>
        services.GetRequiredService<TokenSessionService>();

    private ClaimsPrincipal Principal(TokenSessionResult result) => new JwtSecurityTokenHandler { MapInboundClaims = false }
        .ValidateToken(result.Tokens.AccessToken, factory.Services.GetRequiredService<JwtKeyMaterial>().ValidationParameters, out _);
}
