using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.ServiceAccounts.Tokens;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceTokenSessionPersistenceTests(SecureHospitalApiFactory factory)
    : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task RateLimit_ShouldReturnTechnicalJsonWithoutCookiesOrCredentials()
    {
        await using var application = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Security:ServiceAuthRateLimit:PermitLimit", "1"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var first = await client.PostAsJsonAsync("/api/service-auth/token", new { apiKey = "invalid-key" });
        first.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var limited = await client.PostAsJsonAsync("/api/service-auth/refresh", new { refreshToken = "invalid-refresh" });

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.CacheControl!.NoStore.Should().BeTrue();
        limited.Headers.Contains("Set-Cookie").Should().BeFalse();
        limited.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var error = (await limited.Content.ReadFromJsonAsync<ServiceAuthErrorDto>())!;
        error.Code.Should().Be(ServiceAuthErrorCodes.RateLimited);
        error.TraceId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task IssueHttpFailureAfterSqlSave_ShouldReturnSafeTechnicalErrorAndRollBackCredentials()
    {
        var account = await AccountAsync();
        byte[] originalHash;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var row = await db.ServiceAccounts.SingleAsync(x => x.Id == account.Account.Id);
            row.ApiKeyHash = ServiceAccountsApiTests.LegacyHashKey(account.ApiKey);
            await db.SaveChangesAsync();
            originalHash = row.ApiKeyHash;
        }
        var fault = new FailAfterSave();
        await using var application = WithInterceptor(fault);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var before = await CountsAsync(account.Account.Id);
        fault.Armed = true;

        using var response = await client.PostAsJsonAsync("/api/service-auth/token", new { apiKey = account.ApiKey });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        var body = await response.Content.ReadAsStringAsync();
        (body.Contains(account.ApiKey, StringComparison.Ordinal) || body.Contains("L8 after-save fault", StringComparison.Ordinal))
            .Should().BeFalse();
        var error = (await response.Content.ReadFromJsonAsync<ServiceAuthErrorDto>())!;
        error.Code.Should().Be(ServiceAuthErrorCodes.InternalError);
        fault.Triggered.Should().BeTrue();
        (await CountsAsync(account.Account.Id)).Should().Be(before);
        await using var verification = factory.Services.CreateAsyncScope();
        var stored = await verification.ServiceProvider.GetRequiredService<HospitalDbContext>().ServiceAccounts
            .AsNoTracking().SingleAsync(x => x.Id == account.Account.Id);
        stored.ApiKeyHash.Should().Equal(originalHash);
    }

    [Fact]
    public async Task Issue_ShouldPersistOnlyRefreshHashAndBindAccessToExactAccountSessionAndFamily()
    {
        var account = await AccountAsync();
        var issued = (await IssueAsync(account.ApiKey))!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var row = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(x => x.ServiceAccountId == account.Account.Id);

        row.TokenHash.Should().Be(Hash(issued.RefreshToken));
        row.AuthenticationVersion.Should().Be(1);
        row.RevokedAtUtc.Should().BeNull();
        row.ConsumedAtUtc.Should().BeNull();
        row.ExpiresAtUtc.Should().Be(issued.RefreshTokenExpiresAt);
        jwt.Subject.Should().Be(account.Account.Id.ToString("D"));
        jwt.Id.Should().Be(row.Id.ToString("D"));
        jwt.Claims.Single(x => x.Type == "sid").Value.Should().Be(row.FamilyId.ToString("D"));
        new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero).Should().Be(issued.AccessTokenExpiresAt);
        (await ValidAsync(issued)).Should().BeTrue();
        (await db.RefreshSessions.AnyAsync(x => x.Id == row.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ShouldKeepOriginalFamilyDeadlineAndOldAccessWhileConsumingOnlyTheCredential()
    {
        var account = await AccountAsync();
        var initial = (await IssueAsync(account.ApiKey))!;
        factory.AdvanceTime(TimeSpan.FromMinutes(1));

        var refreshed = (await RefreshAsync(initial.RefreshToken))!;

        refreshed.RefreshTokenExpiresAt.Should().Be(initial.RefreshTokenExpiresAt);
        refreshed.AccessTokenExpiresAt.Should().BeAfter(initial.AccessTokenExpiresAt);
        (await ValidAsync(initial)).Should().BeTrue();
        (await ValidAsync(refreshed)).Should().BeTrue();
        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().ServiceRefreshSessions
            .AsNoTracking().Where(x => x.ServiceAccountId == account.Account.Id).ToArrayAsync();
        rows.Should().HaveCount(2);
        rows.Select(x => x.FamilyId).Distinct().Should().ContainSingle();
        rows.Should().OnlyContain(x => x.RevokedAtUtc == null && x.ExpiresAtUtc == initial.RefreshTokenExpiresAt);
        var previous = rows.Single(x => x.TokenHash == Hash(initial.RefreshToken));
        previous.ConsumedAtUtc.Should().NotBeNull();
        previous.ReplacedBySessionId.Should().Be(rows.Single(x => x.TokenHash == Hash(refreshed.RefreshToken)).Id);
    }

    [Fact]
    public async Task NearFamilyDeadline_AccessMustNotOutliveRefreshAndExactExpiryMustRejectFurtherRefresh()
    {
        var account = await AccountAsync();
        var initial = (await IssueAsync(account.ApiKey))!;
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        factory.AdvanceTime(initial.RefreshTokenExpiresAt - now - TimeSpan.FromMinutes(1));

        var last = (await RefreshAsync(initial.RefreshToken))!;

        last.AccessTokenExpiresAt.Should().BeOnOrBefore(initial.RefreshTokenExpiresAt);
        last.RefreshTokenExpiresAt.Should().Be(initial.RefreshTokenExpiresAt);
        factory.AdvanceTime(initial.RefreshTokenExpiresAt - factory.Services.GetRequiredService<TimeProvider>().GetUtcNow());
        (await RefreshAsync(last.RefreshToken)).Should().BeNull();
        var validate = () => Parse(last.AccessToken);
        validate.Should().Throw<Microsoft.IdentityModel.Tokens.SecurityTokenInvalidLifetimeException>();
    }

    [Fact]
    public async Task Reuse_ShouldRevokeOnlyItsFamilyAndKeepSeparateConnectionWorking()
    {
        var account = await AccountAsync();
        var first = (await IssueAsync(account.ApiKey))!;
        var separate = (await IssueAsync(account.ApiKey))!;
        var replacement = (await RefreshAsync(first.RefreshToken))!;

        (await RefreshAsync(first.RefreshToken)).Should().BeNull();

        (await ValidAsync(first)).Should().BeFalse();
        (await ValidAsync(replacement)).Should().BeFalse();
        (await ValidAsync(separate)).Should().BeTrue();
        (await RefreshAsync(replacement.RefreshToken)).Should().BeNull();
        (await RefreshAsync(separate.RefreshToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentRefresh_ShouldCreateOneContinuationAndRevokeItWhenReuseIsDetected()
    {
        var account = await AccountAsync();
        var initial = (await IssueAsync(account.ApiKey))!;
        var gate = new AccountLockGate(account.Account.Id, 2);
        await using var application = WithInterceptor(gate);
        _ = application.Services;
        await using var owner = factory.Services.CreateAsyncScope();
        var db = owner.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        _ = await db.ServiceAccounts.FromSqlInterpolated(
            $"SELECT *, xmin FROM service_accounts WHERE id = {account.Account.Id} FOR UPDATE")
            .AsNoTracking().SingleAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<ServiceTokenPairDto?> AttemptAsync()
        {
            await using var scope = application.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>()
                .RefreshAsync(initial.RefreshToken, timeout.Token);
        }
        var first = Task.Run(AttemptAsync, timeout.Token);
        var second = Task.Run(AttemptAsync, timeout.Token);
        await gate.Entered.Task.WaitAsync(timeout.Token);
        await transaction.CommitAsync(timeout.Token);

        var attempts = await Task.WhenAll(first, second);

        attempts.Count(result => result is not null).Should().Be(1);
        var continuation = attempts.Single(result => result is not null)!;
        (await ValidAsync(initial)).Should().BeFalse();
        (await ValidAsync(continuation)).Should().BeFalse();
        await using var verify = factory.Services.CreateAsyncScope();
        var rows = await verify.ServiceProvider.GetRequiredService<HospitalDbContext>().ServiceRefreshSessions
            .AsNoTracking().Where(row => row.ServiceAccountId == account.Account.Id).ToArrayAsync();
        rows.Should().HaveCount(2).And.OnlyContain(row => row.RevokedAtUtc != null &&
            row.RevokeReason == "service-refresh-reuse");
        rows.Select(row => row.FamilyId).Distinct().Should().ContainSingle();
        (await RefreshAsync(continuation.RefreshToken)).Should().BeNull();
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("sid")]
    [InlineData("jti")]
    [InlineData("auth_ver")]
    public async Task ValidSignatureWithForeignSessionBinding_ShouldNotValidate(string claimType)
    {
        var first = await AccountAsync();
        var other = await AccountAsync();
        var initial = (await IssueAsync(first.ApiKey))!;
        var separate = (await IssueAsync(other.ApiKey))!;
        var principal = Parse(initial.AccessToken);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(claimType)!);
        identity.AddClaim(new Claim(claimType, claimType == "auth_ver" ? "2" :
            Parse(separate.AccessToken).FindFirst(claimType)!.Value));

        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>()
            .ValidatePrincipalAsync(principal, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlFailureAfterSave_ShouldRollBackNewSessionConsumptionAndAudit(bool refreshing)
    {
        var account = await AccountAsync();
        var original = refreshing ? (await IssueAsync(account.ApiKey))! : null;
        var fault = new FailAfterSave();
        await using var application = WithInterceptor(fault);
        _ = application.Services;
        var before = await CountsAsync(account.Account.Id);
        fault.Armed = true;

        var action = async () =>
        {
            await using var scope = application.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>();
            if (refreshing) await service.RefreshAsync(original!.RefreshToken, CancellationToken.None);
            else await service.AuthenticateAsync(account.ApiKey, CancellationToken.None);
        };

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("L8 after-save fault");
        fault.Triggered.Should().BeTrue();
        (await CountsAsync(account.Account.Id)).Should().Be(before);
        if (original is not null)
        {
            (await ValidAsync(original)).Should().BeTrue();
            (await RefreshAsync(original.RefreshToken)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task FailedReuseAudit_ShouldRollBackRevocationAndKeepReplacementUsable()
    {
        var account = await AccountAsync();
        var original = (await IssueAsync(account.ApiKey))!;
        var replacement = (await RefreshAsync(original.RefreshToken))!;
        var fault = new FailAfterSave();
        await using var application = WithInterceptor(fault);
        _ = application.Services;
        fault.Armed = true;
        var action = async () =>
        {
            await using var scope = application.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>()
                .RefreshAsync(original.RefreshToken, CancellationToken.None);
        };

        await action.Should().ThrowAsync<InvalidOperationException>();

        fault.Triggered.Should().BeTrue();
        (await ValidAsync(replacement)).Should().BeTrue();
        (await RefreshAsync(replacement.RefreshToken)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialsWaitingForAccountLock_ShouldObserveCommittedRotationOrDeactivation(bool refreshing)
    {
        var account = await AccountAsync();
        var issued = refreshing ? (await IssueAsync(account.ApiKey))! : null;
        var gate = new AccountLockGate(account.Account.Id);
        await using var application = WithInterceptor(gate);
        _ = application.Services;
        await using var owner = factory.Services.CreateAsyncScope();
        var db = owner.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        _ = await db.ServiceAccounts.FromSqlInterpolated(
            $"SELECT *, xmin FROM service_accounts WHERE id = {account.Account.Id} FOR UPDATE")
            .AsNoTracking().SingleAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waiting = Task.Run(async () =>
        {
            await using var scope = application.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>();
            return refreshing ? await service.RefreshAsync(issued!.RefreshToken, timeout.Token) :
                await service.AuthenticateAsync(account.ApiKey, timeout.Token);
        }, timeout.Token);
        await gate.Entered.Task.WaitAsync(timeout.Token);
        var management = owner.ServiceProvider.GetRequiredService<IServiceAccountService>();
        if (refreshing)
            await management.SetActiveAsync(account.Account.Id,
                new SetServiceAccountActiveRequest(false, account.Account.Version), timeout.Token);
        else
            await management.RotateKeyAsync(account.Account.Id,
                new RotateServiceAccountKeyRequest(account.Account.Version), timeout.Token);
        await transaction.CommitAsync(timeout.Token);

        (await waiting).Should().BeNull();
        await using var verify = factory.Services.CreateAsyncScope();
        var sessions = await verify.ServiceProvider.GetRequiredService<HospitalDbContext>()
            .ServiceRefreshSessions.AsNoTracking().Where(x => x.ServiceAccountId == account.Account.Id).ToArrayAsync();
        sessions.Should().HaveCount(refreshing ? 1 : 0);
        if (refreshing) sessions.Should().OnlyContain(x => x.RevokedAtUtc != null);
    }

    private WebApplicationFactory<Program> WithInterceptor(IInterceptor interceptor) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services => services.AddDbContext<HospitalDbContext>(options =>
            options.AddInterceptors(interceptor))));

    private async Task<ServiceAccountKeyResultDto> AccountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
            .CreateAsync(new CreateServiceAccountRequest("session-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
    }

    private async Task<ServiceTokenPairDto?> IssueAsync(string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>()
            .AuthenticateAsync(key, CancellationToken.None);
    }

    private async Task<ServiceTokenPairDto?> RefreshAsync(string token)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>()
            .RefreshAsync(token, CancellationToken.None);
    }

    private async Task<bool> ValidAsync(ServiceTokenPairDto pair)
    {
        var principal = Parse(pair.AccessToken);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ServiceTokenSessionService>()
            .ValidatePrincipalAsync(principal, CancellationToken.None);
    }

    private ClaimsPrincipal Parse(string token) => new JwtSecurityTokenHandler { MapInboundClaims = false }
        .ValidateToken(token, factory.Services.GetRequiredService<ServiceTokenValidationParameters>().ValidationParameters, out _);

    private async Task<(int Sessions, int Audits)> CountsAsync(Guid accountId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return (await db.ServiceRefreshSessions.CountAsync(x => x.ServiceAccountId == accountId),
            await db.AuditEvents.CountAsync(x => x.ActorServiceAccountId == accountId));
    }

    private static string Hash(string refresh) => Convert.ToHexStringLower(SHA256.HashData(
        Convert.FromBase64String(refresh.Replace('-', '+').Replace('_', '/') + "=")));

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Triggered = true;
                throw new InvalidOperationException("L8 after-save fault");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AccountLockGate(Guid accountId, int requiredArrivals = 1) : DbCommandInterceptor
    {
        private int _arrivals;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("service_accounts", StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(x => x.Value is Guid id && id == accountId) &&
                Interlocked.Increment(ref _arrivals) >= requiredArrivals)
                Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
