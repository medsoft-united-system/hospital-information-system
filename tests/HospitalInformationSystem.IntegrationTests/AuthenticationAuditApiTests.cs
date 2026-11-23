using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.MedicalWorkers;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MedicalWorkerRole = HospitalInformationSystem.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AuthenticationAuditApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task Login_AdministratorCredentials_ShouldPersistVerifiedLoginWithoutSecrets()
    {
        using var client = factory.CreateCookieClient();
        var before = await EventIdsAsync(factory.Services, AuditEventType.AdministratorLogin);

        await LoginAsync(client, factory.AdministratorLogin());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(account => account.IsAdministrator, CancellationToken.None);
        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(audit =>
            audit.EventType == nameof(AuditEventType.AdministratorLogin) && !before.Contains(audit.EventId),
            CancellationToken.None);
        entry.ActorKind.Should().Be("user");
        entry.ActorUserId.Should().Be(user.Id);
        entry.ActorDisplayName.Should().Be(SecureHospitalApiFactory.AdminUserName);
        entry.RequestId.Should().NotBeNullOrWhiteSpace();
        var metadata = Metadata(entry);
        metadata.GetProperty("targetId").GetGuid().Should().Be(user.Id);
        metadata.GetProperty("outcome").GetString().Should().Be("success");
        metadata.GetProperty("correlationId").GetString().Should().Be(entry.RequestId);
        AssertNoSecrets(entry, SecureHospitalApiFactory.AdminPassword);
    }

    [Fact]
    public async Task Login_UnknownCredentials_ShouldPersistOneAnonymousFailure()
    {
        using var client = factory.CreateCookieClient();
        await RefreshCsrfAsync(client);
        var attemptedName = "missing" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Unknown.Password-293!";

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(attemptedName, password));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var entries = await FailedEventsForAsync(attemptedName);
        var entry = entries.Should().ContainSingle().Subject;
        entry.ActorKind.Should().Be("anonymous");
        entry.ActorUserId.Should().BeNull();
        entry.ActorDisplayName.Should().NotBe(attemptedName);
        entry.RequestId.Should().NotBeNullOrWhiteSpace();
        Metadata(entry).GetProperty("attemptedUserName").GetString().Should().Be(attemptedName);
        AssertNoSecrets(entry, password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_InvalidPasswordOrPortal_ShouldRecordAnonymousFailureAndIssueNoSession(bool wrongPortal)
    {
        using var client = factory.CreateCookieClient();
        await RefreshCsrfAsync(client);
        var before = await EventIdsAsync(factory.Services, AuditEventType.LoginFailed);
        var password = wrongPortal ? SecureHospitalApiFactory.AdminPassword : "Invalid.Password-293!";

        var response = await client.PostAsJsonAsync("/api/auth/login",
            factory.AdministratorLogin() with
            {
                Password = password, Portal = wrongPortal ? LoginPortal.MedicalWorker : LoginPortal.Administrator
            });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(audit =>
            audit.EventType == nameof(AuditEventType.LoginFailed) && !before.Contains(audit.EventId),
            CancellationToken.None);
        entry.ActorKind.Should().Be("anonymous");
        entry.ActorUserId.Should().BeNull();
        Metadata(entry).GetProperty("attemptedUserName").GetString().Should().Be(SecureHospitalApiFactory.AdminUserName);
        AssertNoSecrets(entry, password);
    }

    [Fact]
    public async Task Login_AuthenticatedClientWithInvalidCredentials_ShouldKeepFailureActorAnonymous()
    {
        using var client = factory.CreateCookieClient();
        await LoginAsync(client, factory.AdministratorLogin());
        var attemptedName = "missing" + Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(attemptedName, "Unknown.Password-293!"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var entry = (await FailedEventsForAsync(attemptedName)).Should().ContainSingle().Subject;
        entry.ActorKind.Should().Be("anonymous");
        entry.ActorUserId.Should().BeNull();
    }

    [Fact]
    public async Task Login_MalformedAttemptedName_ShouldBoundMetadataAndRemoveControlCharacters()
    {
        using var client = factory.CreateCookieClient();
        await RefreshCsrfAsync(client);
        var marker = "invalid" + Guid.NewGuid().ToString("N")[..8];
        var attemptedName = marker + "\n" + new string('x', 200);
        var sanitizedName = marker + new string('x', 128 - marker.Length);
        const string password = "Never.Store-Password.912!";
        const string totpCode = "083219";

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(attemptedName, password, totpCode));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var entry = (await FailedEventsForAsync(sanitizedName)).Should().ContainSingle().Subject;
        var savedName = Metadata(entry).GetProperty("attemptedUserName").GetString()!;
        savedName.Length.Should().Be(128);
        savedName.Any(char.IsControl).Should().BeFalse();
        AssertNoSecrets(entry, password, totpCode);
    }

    [Fact]
    public async Task Login_MedicalWorkerCredentials_ShouldRecordUserLoginAndDenyAuditAccess()
    {
        using var administrator = factory.CreateCookieClient();
        await LoginAsync(administrator, factory.AdministratorLogin());
        var workerResponse = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Иван", "Проверочный", "Врач", MedicalWorkerRole.Doctor));
        workerResponse.EnsureSuccessStatusCode();
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        var accountResponse = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("doctor" + Guid.NewGuid().ToString("N")[..8]));
        accountResponse.EnsureSuccessStatusCode();
        var account = (await accountResponse.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
        using var clinician = factory.CreateCookieClient();

        await LoginAsync(clinician, new LoginRequest(account.Account.UserName, account.GeneratedPassword,
            Portal: LoginPortal.MedicalWorker));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(audit =>
            audit.EventType == nameof(AuditEventType.UserLogin) && audit.ActorUserId == account.Account.Id,
            CancellationToken.None);
        entry.ActorKind.Should().Be("user");
        Metadata(entry).GetProperty("medicalWorkerId").GetGuid().Should().Be(worker.Id);
        AssertNoSecrets(entry, account.GeneratedPassword);
        (await clinician.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonymous = factory.CreateCookieClient();
        (await anonymous.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_RequestsBeyondRateLimit_ShouldRecordRejectedRequestExactlyOnce()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Security:LoginRateLimit:PermitLimit", "2"));
        using var client = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });
        await RefreshCsrfAsync(client);
        var attemptedName = "missing" + Guid.NewGuid().ToString("N")[..8];
        var credentials = new LoginRequest(attemptedName, "Unknown.Password-293!");
        var before = await EventIdsAsync(limitedFactory.Services, AuditEventType.LoginFailed);

        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        await using var scope = limitedFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entries = await db.AuditEvents.AsNoTracking().Where(audit =>
            audit.EventType == nameof(AuditEventType.LoginFailed) && !before.Contains(audit.EventId))
            .ToListAsync(CancellationToken.None);
        entries.Should().HaveCount(3);
        var limited = entries.Where(entry => Metadata(entry).GetProperty("outcome").GetString() == "rate-limited")
            .Should().ContainSingle().Subject;
        limited.ActorKind.Should().Be("anonymous");
        limited.ActorUserId.Should().BeNull();
        Metadata(limited).TryGetProperty("attemptedUserName", out _).Should().BeFalse();
        entries.Select(entry => entry.RequestId).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task Logout_AuthenticatedRequest_ShouldRecordUserAndRemoveAccess()
    {
        using var client = factory.CreateCookieClient();
        await LoginAsync(client, factory.AdministratorLogin());
        var before = await EventIdsAsync(factory.Services, AuditEventType.UserLogout);

        (await client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(audit =>
            audit.EventType == nameof(AuditEventType.UserLogout) && !before.Contains(audit.EventId),
            CancellationToken.None);
        entry.ActorKind.Should().Be("user");
        entry.ActorUserId.Should().NotBeNull();
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<IReadOnlyList<AuditEventDbo>> FailedEventsForAsync(string attemptedName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var entries = await db.AuditEvents.AsNoTracking().Where(entry => entry.EventType == nameof(AuditEventType.LoginFailed))
            .ToListAsync(CancellationToken.None);
        return entries.Where(entry => Metadata(entry).TryGetProperty("attemptedUserName", out var name) &&
                                      name.GetString() == attemptedName).ToArray();
    }

    private static async Task<HashSet<Guid>> EventIdsAsync(IServiceProvider services, AuditEventType type)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var name = type.ToString();
        return (await db.AuditEvents.AsNoTracking().Where(entry => entry.EventType == name)
            .Select(entry => entry.EventId).ToArrayAsync(CancellationToken.None)).ToHashSet();
    }

    private static JsonElement Metadata(AuditEventDbo entry) => JsonSerializer.Deserialize<JsonElement>(entry.MetadataJson);

    private static void AssertNoSecrets(AuditEventDbo entry, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            entry.Message.Should().NotContain(secret);
            entry.MetadataJson.Should().NotContain(secret);
        }
        Metadata(entry).EnumerateObject().Select(property => property.Name).Should().NotContain(
            new[] { "password", "passwordHash", "totp", "totpCode", "secret", "accessToken", "refreshToken" });
    }

    private static async Task LoginAsync(HttpClient client, LoginRequest request)
    {
        await RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", request)).EnsureSuccessStatusCode();
        await RefreshCsrfAsync(client);
    }

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
