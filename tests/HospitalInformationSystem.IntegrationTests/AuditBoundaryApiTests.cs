using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Diagnoses;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MedicalWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AuditBoundaryApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Login_MissingOrInvalidCsrf_ShouldAuditOneAnonymousRejection(
        bool authenticated, bool invalidToken)
    {
        using var client = factory.CreateCookieClient();
        if (authenticated) await LoginAsync(client, factory.AdministratorLogin(), CancellationToken.None);
        else await SetCsrfAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (invalidToken) client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid-csrf-value");
        var before = await EventIdsAsync(factory.Services, CancellationToken.None);
        var attemptedName = "hidden" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Never.Log-Csrf-Rejected-Password.219!";
        const string code = "012345";

        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(attemptedName, password, code), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = (await NewEventsAsync(factory.Services, before, CancellationToken.None))
            .Should().ContainSingle().Subject;
        entry.EventType.Should().Be(nameof(AuditEventType.LoginFailed));
        entry.ActorKind.Should().Be("anonymous");
        entry.ActorUserId.Should().BeNull();
        entry.RequestId.Should().NotBeNullOrWhiteSpace();
        var metadata = Metadata(entry);
        metadata.GetProperty("outcome").GetString().Should().Be("csrf-rejected");
        metadata.GetProperty("action").GetString().Should().Be("login");
        metadata.GetProperty("correlationId").GetString().Should().Be(entry.RequestId);
        metadata.TryGetProperty("attemptedUserName", out _).Should().BeFalse();
        foreach (var secret in new[] { attemptedName, password, "invalid-csrf-value" })
        {
            entry.Message.Should().NotContain(secret);
            entry.MetadataJson.Should().NotContain(secret);
        }
        entry.MetadataJson.Should().NotContain($"\"{code}\"");
        (await client.GetAsync("/api/auth/session", CancellationToken.None)).StatusCode.Should()
            .Be(authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_RateLimitAfterCsrfRejection_ShouldAuditEachRejectedRequestExactlyOnce()
    {
        using var limited = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Security:LoginRateLimit:PermitLimit", "1"));
        using var client = limited.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var before = await EventIdsAsync(limited.Services, CancellationToken.None);
        var credentials = new LoginRequest("unknown", "Never.Log-Limited.182!");

        (await client.PostAsJsonAsync("/api/auth/login", credentials, CancellationToken.None))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/auth/login", credentials, CancellationToken.None))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var entries = await NewEventsAsync(limited.Services, before, CancellationToken.None);
        entries.Should().HaveCount(2).And.OnlyContain(entry =>
            entry.EventType == nameof(AuditEventType.LoginFailed) && entry.ActorKind == "anonymous" &&
            entry.ActorUserId == null);
        entries.Select(entry => Metadata(entry).GetProperty("outcome").GetString()).Should()
            .BeEquivalentTo("csrf-rejected", "rate-limited");
        entries.Select(entry => entry.RequestId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task MutateCatalog_MissingCsrf_ShouldNotReportAnUnrelatedLoginFailure()
    {
        using var client = factory.CreateCookieClient();
        await LoginAsync(client, factory.AdministratorLogin(), CancellationToken.None);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var before = await EventIdsAsync(factory.Services, CancellationToken.None);

        using var response = await client.PostAsJsonAsync("/api/diagnoses",
            new SaveDiagnosisRequest("CSRF99", "Проверка"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await NewEventsAsync(factory.Services, before, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeRole_WorkerWithoutAccount_ShouldAuditRoleChangeAndZeroRevocations()
    {
        using var administrator = factory.CreateCookieClient();
        await LoginAsync(administrator, factory.AdministratorLogin(), CancellationToken.None);
        var worker = await CreateWorkerAsync(administrator, CancellationToken.None);
        var before = await EventIdsAsync(factory.Services, CancellationToken.None);

        using var response = await administrator.PutAsJsonAsync($"/api/medical-workers/{worker.Id}",
            new SaveMedicalWorkerRequest("Врач", "Проверка", "Врач", MedicalWorkerRole.Nurse), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await NewEventsAsync(factory.Services, before, CancellationToken.None))
            .Should().ContainSingle().Subject;
        entry.EventType.Should().Be(nameof(AuditEventType.MedicalWorkerUpdated));
        var metadata = Metadata(entry);
        metadata.GetProperty("action").GetString().Should().Be("medical-role-changed");
        metadata.GetProperty("targetId").GetGuid().Should().Be(worker.Id);
        metadata.GetProperty("medicalWorkerId").GetGuid().Should().Be(worker.Id);
        metadata.GetProperty("revokeReason").GetString().Should().Be("medical-role-changed");
        metadata.GetProperty("revokedRefreshTokenCount").GetInt32().Should().Be(0);
        metadata.TryGetProperty("accountId", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeRole_LinkedAccount_ShouldKeepActorCorrelationAndExactRevocationCount(bool signedIn)
    {
        using var administrator = factory.CreateCookieClient();
        await LoginAsync(administrator, factory.AdministratorLogin(), CancellationToken.None);
        var worker = await CreateWorkerAsync(administrator, CancellationToken.None);
        using var created = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("role" + Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);
        created.EnsureSuccessStatusCode();
        var account = (await created.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>(CancellationToken.None))!;
        using var clinician = factory.CreateCookieClient();
        if (signedIn)
            await LoginAsync(clinician, new LoginRequest(account.Account.UserName, account.GeneratedPassword),
                CancellationToken.None);
        var session = (await administrator.GetFromJsonAsync<SessionDto>("/api/auth/session", CancellationToken.None))!;
        var before = await EventIdsAsync(factory.Services, CancellationToken.None);

        using var updated = await administrator.PutAsJsonAsync($"/api/medical-workers/{worker.Id}",
            new SaveMedicalWorkerRequest("Врач", "Проверка", "Врач", MedicalWorkerRole.Nurse), CancellationToken.None);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await NewEventsAsync(factory.Services, before, CancellationToken.None);
        entries.Should().HaveCount(2).And.OnlyContain(entry => entry.ActorKind == "user" &&
            entry.ActorUserId == session.Id);
        entries.Select(entry => entry.EventType).Should().BeEquivalentTo(
            nameof(AuditEventType.MedicalWorkerUpdated), nameof(AuditEventType.AccountChanged));
        entries.Select(entry => entry.RequestId).Distinct().Should().ContainSingle().Which.Should()
            .NotBeNullOrWhiteSpace();
        foreach (var entry in entries)
        {
            var metadata = Metadata(entry);
            metadata.GetProperty("action").GetString().Should().Be("medical-role-changed");
            metadata.GetProperty("revokeReason").GetString().Should().Be("medical-role-changed");
            metadata.GetProperty("revokedRefreshTokenCount").GetInt32().Should().Be(signedIn ? 1 : 0);
            metadata.GetProperty("correlationId").GetString().Should().Be(entry.RequestId);
        }
        if (signedIn)
            (await clinician.GetAsync("/api/patients", CancellationToken.None)).StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangeRole_AuditDatabaseFailure_ShouldRollbackRoleStampAndSessionRevocation()
    {
        using var administrator = factory.CreateCookieClient();
        await LoginAsync(administrator, factory.AdministratorLogin(), CancellationToken.None);
        var worker = await CreateWorkerAsync(administrator, CancellationToken.None);
        using var created = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("rollback" + Guid.NewGuid().ToString("N")[..8]), CancellationToken.None);
        created.EnsureSuccessStatusCode();
        var account = (await created.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>(CancellationToken.None))!;
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(account.Account.UserName, account.GeneratedPassword),
            CancellationToken.None);
        var before = await EventIdsAsync(factory.Services, CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var stamp = await db.Users.Where(user => user.Id == account.Account.Id)
            .Select(user => user.SecurityStamp).SingleAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_role_audit_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.event_type = 'MedicalWorkerUpdated' THEN RAISE EXCEPTION 'Synthetic audit storage failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_role_audit BEFORE INSERT ON security_audit_events
                FOR EACH ROW EXECUTE FUNCTION reject_role_audit_insert();
            """, CancellationToken.None);
        try
        {
            using var updated = await administrator.PutAsJsonAsync($"/api/medical-workers/{worker.Id}",
                new SaveMedicalWorkerRequest("Врач", "Проверка", "Врач", MedicalWorkerRole.Nurse), CancellationToken.None);
            updated.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_role_audit ON security_audit_events;
                DROP FUNCTION reject_role_audit_insert();
                """, CancellationToken.None);
        }
        (await db.MedicalWorkers.AsNoTracking().SingleAsync(item => item.Id == worker.Id, CancellationToken.None))
            .Role.Should().Be(Application.Models.MedicalWorkers.MedicalWorkerRole.Doctor);
        (await db.Users.Where(user => user.Id == account.Account.Id).Select(user => user.SecurityStamp)
            .SingleAsync(CancellationToken.None)).Should().Be(stamp);
        (await db.RefreshSessions.CountAsync(session => session.UserId == account.Account.Id &&
            session.RevokedAtUtc == null, CancellationToken.None)).Should().Be(1);
        (await NewEventsAsync(factory.Services, before, CancellationToken.None)).Should().BeEmpty();
        (await clinician.GetAsync("/api/patients", CancellationToken.None)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<MedicalWorkerDto> CreateWorkerAsync(HttpClient administrator,
        CancellationToken cancellationToken)
    {
        using var response = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Врач", "Проверка", "Врач", MedicalWorkerRole.Doctor), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicalWorkerDto>(cancellationToken))!;
    }

    private static async Task<HashSet<Guid>> EventIdsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return (await db.AuditEvents.Select(entry => entry.EventId).ToArrayAsync(cancellationToken)).ToHashSet();
    }

    private static async Task<IReadOnlyList<AuditEventDbo>> NewEventsAsync(IServiceProvider services,
        HashSet<Guid> previous, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents.AsNoTracking()
            .Where(entry => !previous.Contains(entry.EventId)).ToArrayAsync(cancellationToken);
    }

    private static JsonElement Metadata(AuditEventDbo entry) => JsonSerializer.Deserialize<JsonElement>(entry.MetadataJson);

    private static async Task LoginAsync(HttpClient client, LoginRequest request, CancellationToken cancellationToken)
    {
        await SetCsrfAsync(client, cancellationToken);
        (await client.PostAsJsonAsync("/api/auth/login", request, cancellationToken)).EnsureSuccessStatusCode();
        await SetCsrfAsync(client, cancellationToken);
    }

    private static async Task SetCsrfAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf", cancellationToken))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
