using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAccountManagementPersistenceTests(SecureHospitalApiFactory factory)
    : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task Rotate_ShouldReplaceOnlyHashAndAtomicallyRevokeAllAccountFamiliesWithAdministratorAudit()
    {
        using var client = await AdministratorAsync();
        var administrator = (await client.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
        var created = await ServiceAccountsApiTests.CreateAsync(client);
        var other = await ServiceAccountsApiTests.CreateAsync(client);
        var sessions = await SeedSessionsAsync(created.Account, other.Account);

        using var response = await client.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key",
            new RotateServiceAccountKeyRequest(created.Account.Version));

        var rotated = await ServiceAccountsApiTests.ReadAsync<ServiceAccountKeyResultDto>(response);
        rotated.Account.Id.Should().Be(created.Account.Id);
        rotated.Account.Version.Should().NotBe(created.Account.Version);
        rotated.Account.KeyPrefix.Should().Be(created.Account.KeyPrefix);
        rotated.RevokedSessionCount.Should().Be(2, "two logical active families exist, including a consumed predecessor");
        (rotated.ApiKey != created.ApiKey).Should().BeTrue("rotation must produce a new credential");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var stored = await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
        ServiceAccountsApiTests.AssertArgonKey(rotated.ApiKey, created.Account.Id, stored.ApiKeyHash);
        new ServiceApiKeyGenerator().Verify(created.ApiKey, created.Account.Id, stored.ApiKeyHash).Should()
            .Be(ServiceApiKeyVerificationResult.Failed);
        stored.AuthenticationVersion.Should().Be(2);
        stored.IsActive.Should().BeTrue();
        await AssertRevokedAsync(db, sessions, "service-key-rotated");
        var audits = await ServiceAccountsApiTests.AuditsAsync(db, created.Account.Id);
        audits.Should().HaveCount(2);
        var rotation = audits.Single(entry => Action(entry) == "service-key-rotated");
        rotation.ActorKind.Should().Be("user");
        rotation.ActorUserId.Should().Be(administrator.Id);
        rotation.ActorServiceAccountId.Should().BeNull();
        using var metadata = JsonDocument.Parse(rotation.MetadataJson);
        metadata.RootElement.GetProperty("revokedServiceSessionCount").GetInt32().Should().Be(2);
        metadata.RootElement.GetProperty("revokeReason").GetString().Should().Be("service-key-rotated");
        (rotation.Message.Contains(rotated.ApiKey, StringComparison.Ordinal) ||
         rotation.MetadataJson.Contains(rotated.ApiKey, StringComparison.Ordinal)).Should().BeFalse();
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeactivateAndReactivate_ShouldRevokeSessionsAndNeverRestoreThemOrThePreviousVersion()
    {
        using var client = await AdministratorAsync();
        var created = await ServiceAccountsApiTests.CreateAsync(client);
        var other = await ServiceAccountsApiTests.CreateAsync(client);
        var sessions = await SeedSessionsAsync(created.Account, other.Account);
        using var stopped = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}/active",
            new SetServiceAccountActiveRequest(false, created.Account.Version));
        var inactive = await ServiceAccountsApiTests.ReadAsync<ServiceAccountActivityResultDto>(stopped);
        inactive.Account.IsActive.Should().BeFalse();
        inactive.RevokedSessionCount.Should().Be(2);
        inactive.Account.Version.Should().NotBe(created.Account.Version);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id))
                .AuthenticationVersion.Should().Be(2);
            await AssertRevokedAsync(db, sessions, "service-account-deactivated");
        }

        using var restarted = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}/active",
            new SetServiceAccountActiveRequest(true, inactive.Account.Version));

        var active = await ServiceAccountsApiTests.ReadAsync<ServiceAccountActivityResultDto>(restarted);
        active.Account.IsActive.Should().BeTrue();
        active.RevokedSessionCount.Should().Be(0);
        await using var verification = factory.Services.CreateAsyncScope();
        var after = verification.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var restored = await after.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
        restored.AuthenticationVersion.Should().BeGreaterThan(1);
        ServiceAccountsApiTests.AssertArgonKey(created.ApiKey, created.Account.Id, restored.ApiKeyHash);
        await AssertRevokedAsync(after, sessions, "service-account-deactivated");
        (await ServiceAccountsApiTests.AuditsAsync(after, created.Account.Id)).Select(Action).Should()
            .BeEquivalentTo(new[] { "service-account-created", "service-account-deactivated", "service-account-activated" });
    }

    [Fact]
    public async Task RenameAndUnchangedActivity_ShouldNotInvalidateSessionsOrProduceNoOpAudit()
    {
        using var client = await AdministratorAsync();
        var created = await ServiceAccountsApiTests.CreateAsync(client);
        var other = await ServiceAccountsApiTests.CreateAsync(client);
        var sessions = await SeedSessionsAsync(created.Account, other.Account);
        using var unchanged = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}/active",
            new SetServiceAccountActiveRequest(true, created.Account.Version));
        var noOp = await ServiceAccountsApiTests.ReadAsync<ServiceAccountActivityResultDto>(unchanged);
        noOp.Account.Should().BeEquivalentTo(created.Account);
        noOp.RevokedSessionCount.Should().Be(0);
        using var renamedResponse = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}",
            new UpdateServiceAccountRequest(ServiceAccountsApiTests.UniqueName(), created.Account.Version));
        var renamed = await ServiceAccountsApiTests.ReadAsync<ServiceAccountDto>(renamedResponse);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var saved = await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
        saved.AuthenticationVersion.Should().Be(1);
        ServiceAccountsApiTests.AssertArgonKey(created.ApiKey, created.Account.Id, saved.ApiKeyHash);
        var rows = await db.ServiceRefreshSessions.AsNoTracking().Where(session => sessions.UnrevokedIds.Contains(session.Id)).ToArrayAsync();
        rows.Should().HaveCount(sessions.UnrevokedIds.Length).And.OnlyContain(session => session.RevokedAtUtc == null);
        (await ServiceAccountsApiTests.AuditsAsync(db, created.Account.Id)).Select(Action).Should()
            .BeEquivalentTo(new[] { "service-account-created", "service-account-renamed" });
        using var noOpRename = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}",
            new UpdateServiceAccountRequest(renamed.Name, renamed.Version));
        (await ServiceAccountsApiTests.ReadAsync<ServiceAccountDto>(noOpRename)).Should().BeEquivalentTo(renamed);
        (await ServiceAccountsApiTests.AuditsAsync(db, created.Account.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ConcurrentRotations_WithSameVersion_ShouldCommitExactlyOneNewKeyAndOneAudit()
    {
        using var first = await AdministratorAsync();
        using var second = await AdministratorAsync();
        var created = await ServiceAccountsApiTests.CreateAsync(first);
        var other = await ServiceAccountsApiTests.CreateAsync(first);
        var sessions = await SeedSessionsAsync(created.Account, other.Account);
        var request = new RotateServiceAccountKeyRequest(created.Account.Version);

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key", request),
            second.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key", request));

        try
        {
            responses.Select(response => response.StatusCode).Should()
                .BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            var winner = await ServiceAccountsApiTests.ReadAsync<ServiceAccountKeyResultDto>(responses.Single(response => response.StatusCode == HttpStatusCode.OK));
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var saved = await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
            ServiceAccountsApiTests.AssertArgonKey(winner.ApiKey, created.Account.Id, saved.ApiKeyHash);
            saved.AuthenticationVersion.Should().Be(2);
            await AssertRevokedAsync(db, sessions, "service-key-rotated");
            (await ServiceAccountsApiTests.AuditsAsync(db, created.Account.Id)).Should().HaveCount(2);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentCreation_WithSameNormalizedName_ShouldCommitExactlyOneAccountAndAudit()
    {
        using var first = await AdministratorAsync();
        using var second = await AdministratorAsync();
        var name = ServiceAccountsApiTests.UniqueName();
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(name)),
            second.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(name.ToUpperInvariant())));
        try
        {
            responses.Select(response => response.StatusCode).Should()
                .BeEquivalentTo(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            var winner = await ServiceAccountsApiTests.ReadAsync<ServiceAccountKeyResultDto>(responses.Single(response => response.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.ServiceAccounts.CountAsync(account => account.NormalizedName == name.ToUpperInvariant())).Should().Be(1);
            (await ServiceAccountsApiTests.AuditsAsync(db, winner.Account.Id)).Should().ContainSingle();
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuditFailure_ShouldRollBackKeyOrActivityVersionAndAllRevocations(bool rotate)
    {
        using var client = await AdministratorAsync();
        var created = await ServiceAccountsApiTests.CreateAsync(client);
        var other = await ServiceAccountsApiTests.CreateAsync(client);
        var sessions = await SeedSessionsAsync(created.Account, other.Account);
        var trigger = "reject_service_audit_" + Guid.NewGuid().ToString("N");
        using var commands = new NpgsqlCommandBuilder();
        var identifier = commands.QuoteIdentifier(trigger);
        var accountId = created.Account.Id.ToString("D");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var createTriggerSql = $"""
            CREATE FUNCTION {identifier}() RETURNS trigger LANGUAGE plpgsql AS $guard$
            BEGIN RAISE EXCEPTION 'Synthetic service audit storage failure'; END $guard$;
            CREATE TRIGGER {identifier} BEFORE INSERT ON security_audit_events FOR EACH ROW
                WHEN ((NEW.metadata ->> 'serviceAccountId') = '{accountId}')
                EXECUTE FUNCTION {identifier}();
            """;
        var dropTriggerSql = $"DROP TRIGGER {identifier} ON security_audit_events; DROP FUNCTION {identifier}();";
        await db.Database.ExecuteSqlRawAsync(createTriggerSql);
        try
        {
            using var response = rotate
                ? await client.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key", new RotateServiceAccountKeyRequest(created.Account.Version))
                : await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}/active", new SetServiceAccountActiveRequest(false, created.Account.Version));
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await response.Content.ReadAsStringAsync()).Contains("msa_", StringComparison.Ordinal).Should().BeFalse();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(dropTriggerSql);
        }
        await AssertUnchangedAsync(created, sessions);
        (await client.GetFromJsonAsync<ServiceAccountDto>($"/api/service-accounts/{created.Account.Id}"))
            .Should().BeEquivalentTo(created.Account);
    }

    [Fact]
    public async Task FailureAfterAccountSqlWriteBeforeCommit_ShouldRollbackKeySessionsAndAudit()
    {
        using var ordinary = await AdministratorAsync();
        var created = await ServiceAccountsApiTests.CreateAsync(ordinary);
        var other = await ServiceAccountsApiTests.CreateAsync(ordinary);
        var sessions = await SeedSessionsAsync(created.Account, other.Account);
        var failure = new FailServiceAccountWriteInterceptor();
        await using var observed = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<HospitalDbContext>(options => options.AddInterceptors(failure))));
        using var client = await AdministratorAsync(observed);

        using var response = await client.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key",
            new RotateServiceAccountKeyRequest(created.Account.Version));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failure.Triggered.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Contains("msa_", StringComparison.Ordinal).Should().BeFalse();
        await AssertUnchangedAsync(created, sessions);
    }

    private async Task AssertUnchangedAsync(ServiceAccountKeyResultDto created, Sessions sessions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var row = await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
        row.IsActive.Should().BeTrue();
        row.AuthenticationVersion.Should().Be(1);
        ServiceAccountsApiTests.AssertArgonKey(created.ApiKey, created.Account.Id, row.ApiKeyHash);
        (await db.ServiceAccounts.Where(account => account.Id == created.Account.Id)
            .Select(account => EF.Property<uint>(account, "xmin")).SingleAsync()).Should().Be(created.Account.Version);
        (await db.ServiceRefreshSessions.AsNoTracking().Where(session => sessions.UnrevokedIds.Contains(session.Id)).ToArrayAsync())
            .Should().HaveCount(sessions.UnrevokedIds.Length).And.OnlyContain(session => session.RevokedAtUtc == null && session.RevokeReason == null);
        (await ServiceAccountsApiTests.AuditsAsync(db, created.Account.Id)).Should().ContainSingle();
    }

    private async Task<Sessions> SeedSessionsAsync(ServiceAccountDto account, ServiceAccountDto otherAccount)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var firstFamily = Guid.NewGuid();
        var current = Session(account.Id, firstFamily, now);
        current.CreatedAtUtc = now.AddDays(-1);
        var consumed = Session(account.Id, firstFamily, now);
        consumed.ConsumedAtUtc = now.AddDays(-1);
        consumed.ReplacedBySessionId = current.Id;
        var independent = Session(account.Id, Guid.NewGuid(), now);
        var expired = Session(account.Id, Guid.NewGuid(), now);
        expired.ExpiresAtUtc = now.AddDays(-1);
        var alreadyRevoked = Session(account.Id, Guid.NewGuid(), now);
        alreadyRevoked.RevokedAtUtc = now.AddHours(-1);
        alreadyRevoked.RevokeReason = "previous-revocation";
        var foreign = Session(otherAccount.Id, Guid.NewGuid(), now);
        db.ServiceRefreshSessions.AddRange(current, consumed, independent, expired, alreadyRevoked, foreign);
        await db.SaveChangesAsync();
        return new Sessions(account.Id, [current.Id, consumed.Id, independent.Id, expired.Id], alreadyRevoked.Id,
            alreadyRevoked.RevokedAtUtc.Value, foreign.Id);
    }

    private static ServiceRefreshSessionDbo Session(Guid accountId, Guid familyId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ServiceAccountId = accountId, FamilyId = familyId,
        TokenHash = Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
        AuthenticationVersion = 1, CreatedAtUtc = now.AddDays(-2), ExpiresAtUtc = now.AddDays(7)
    };

    private static async Task AssertRevokedAsync(HospitalDbContext db, Sessions expected, string reason)
    {
        var rows = await db.ServiceRefreshSessions.AsNoTracking().Where(session => expected.UnrevokedIds.Contains(session.Id)).ToArrayAsync();
        rows.Should().HaveCount(expected.UnrevokedIds.Length).And.OnlyContain(session => session.RevokedAtUtc != null && session.RevokeReason == reason);
        var previous = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(session => session.Id == expected.PreviouslyRevokedId);
        previous.RevokedAtUtc.Should().NotBeNull();
        previous.RevokedAtUtc!.Value.Should().BeCloseTo(expected.PreviousRevocation, TimeSpan.FromTicks(10));
        previous.RevokeReason.Should().Be("previous-revocation");
        var foreign = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(session => session.Id == expected.OtherAccountSessionId);
        foreign.RevokedAtUtc.Should().BeNull();
        foreign.RevokeReason.Should().BeNull();
    }

    private async Task<HttpClient> AdministratorAsync(WebApplicationFactory<Program>? application = null)
    {
        var client = application is null ? factory.CreateCookieClient()
            : application.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
        await ServiceAccountsApiTests.SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin())).EnsureSuccessStatusCode();
        await ServiceAccountsApiTests.SetCsrfAsync(client);
        return client;
    }

    private static string? Action(AuditEventDbo audit)
    {
        using var document = JsonDocument.Parse(audit.MetadataJson);
        return document.RootElement.GetProperty("action").GetString();
    }

    private sealed record Sessions(Guid AccountId, Guid[] UnrevokedIds, Guid PreviouslyRevokedId,
        DateTimeOffset PreviousRevocation, Guid OtherAccountSessionId);

    private sealed class FailServiceAccountWriteInterceptor : DbCommandInterceptor
    {
        private int _triggered;
        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            FailAfterAccountWrite(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            FailAfterAccountWrite(command);
            return ValueTask.FromResult(result);
        }

        private void FailAfterAccountWrite(DbCommand command)
        {
            if ((command.CommandText.Contains("UPDATE service_accounts", StringComparison.Ordinal) ||
                 command.CommandText.Contains("UPDATE \"service_accounts\"", StringComparison.Ordinal)) &&
                Interlocked.Exchange(ref _triggered, 1) == 0)
                throw new InvalidOperationException("Synthetic failure after service-account SQL write and before transaction commit.");
        }
    }
}
