using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AccountLifecycleApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task CreateAccount_ActiveWorker_ShouldReturnGeneratedPasswordOnceAndRejectDuplicateBinding()
    {
        using var admin = await AdminAsync();
        var created = await CreateAsync(admin);

        created.GeneratedPassword.Should().MatchRegex("^[A-Za-z0-9]{12}$");
        using var listed = await admin.GetAsync("/api/accounts");
        listed.EnsureSuccessStatusCode();
        var body = await listed.Content.ReadAsStringAsync();
        body.Should().NotContain(created.GeneratedPassword).And.NotContain("passwordHash").And.NotContain("generatedPassword");
        using var duplicate = await admin.PostAsJsonAsync($"/api/medical-workers/{created.Account.MedicalWorkerId}/account",
            new CreateWorkerAccountRequest("dup." + Guid.NewGuid().ToString("N")[..12]));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == created.Account.Id);
        user.PasswordHash.Should().StartWith("$argon2id$");
        (await db.Users.CountAsync(item => item.MedicalWorkerId == created.Account.MedicalWorkerId)).Should().Be(1);
        (await scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>()
            .CheckPasswordAsync(user, created.GeneratedPassword)).Should().BeTrue();
    }

    [Fact]
    public async Task ResetPassword_ExistingSessions_ShouldRevokeLiveRefreshAndOldCookieWithoutExposingStoredSecrets()
    {
        using var admin = await AdminAsync();
        var created = await CreateAsync(admin);
        using var worker = factory.CreateCookieClient();
        await LoginAsync(worker, created.Account.UserName, created.GeneratedPassword);
        await SeedSessionsAsync(created.Account.Id);

        using var response = await admin.PostAsync($"/api/accounts/{created.Account.Id}/reset-password", null);

        response.EnsureSuccessStatusCode();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var result = (await response.Content.ReadFromJsonAsync<ResetAccountPasswordResultDto>())!;
        result.GeneratedPassword.Should().NotBe(created.GeneratedPassword).And.MatchRegex("^[A-Za-z0-9]{12}$");
        result.RevokedRefreshTokenCount.Should().Be(3, "два подготовленных refresh и одна реальная сессия входа");
        (await worker.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
        var user = await db.Users.SingleAsync(item => item.Id == created.Account.Id);
        (await users.CheckPasswordAsync(user, created.GeneratedPassword)).Should().BeFalse();
        (await users.CheckPasswordAsync(user, result.GeneratedPassword)).Should().BeTrue();
        var auditEvents = await db.AuditEvents.Where(item => item.EventType == nameof(AuditEventType.AccountChanged))
            .OrderByDescending(item => item.EventTimeUtc).ToArrayAsync();
        var audit = auditEvents.First(item => item.MetadataJson.Contains(created.Account.Id.ToString()));
        audit.MetadataJson.Should().NotContain(result.GeneratedPassword).And.NotContain(created.GeneratedPassword);
        using var metadata = JsonDocument.Parse(audit.MetadataJson);
        metadata.RootElement.GetProperty("revokedRefreshTokenCount").GetInt32().Should().Be(3);
        metadata.RootElement.GetProperty("revokeReason").GetString().Should().Be("password-reset");
        audit.ActorUserId.Should().NotBe(created.Account.Id);
    }

    [Fact]
    public async Task SetActivity_DeactivateThenReactivate_ShouldPreservePasswordAndNeverRestoreRefreshSessions()
    {
        using var admin = await AdminAsync();
        var created = await CreateAsync(admin);
        await SeedSessionsAsync(created.Account.Id);

        using var deactivated = await admin.PutAsJsonAsync($"/api/accounts/{created.Account.Id}/activity",
            new SetAccountActiveRequest(false));
        deactivated.EnsureSuccessStatusCode();
        var off = (await deactivated.Content.ReadFromJsonAsync<SetAccountActiveResultDto>())!;
        off.Account.IsActive.Should().BeFalse();
        off.RevokedRefreshTokenCount.Should().Be(2);
        using var reactivated = await admin.PutAsJsonAsync($"/api/accounts/{created.Account.Id}/activity",
            new SetAccountActiveRequest(true));
        reactivated.EnsureSuccessStatusCode();
        var on = (await reactivated.Content.ReadFromJsonAsync<SetAccountActiveResultDto>())!;
        on.Account.IsActive.Should().BeTrue();
        on.RevokedRefreshTokenCount.Should().Be(0);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == created.Account.Id);
        (await scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>()
            .CheckPasswordAsync(user, created.GeneratedPassword)).Should().BeTrue();
        (await db.RefreshSessions.CountAsync(item => item.UserId == user.Id && item.RevokedAtUtc == null &&
            item.ExpiresAtUtc > DateTimeOffset.UtcNow)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeWorker_RoleChangeOrDeletion_ShouldRevokeRefreshAtomicallyAndPreserveAccountBinding(bool delete)
    {
        using var admin = await AdminAsync();
        var created = await CreateAsync(admin);
        await SeedSessionsAsync(created.Account.Id);

        using var response = delete
            ? await admin.DeleteAsync($"/api/medical-workers/{created.Account.MedicalWorkerId}")
            : await admin.PutAsJsonAsync($"/api/medical-workers/{created.Account.MedicalWorkerId}",
                new SaveMedicalWorkerRequest("Лев", "Проверочный", "Медсестра", MedicalWorkerRole.Nurse));

        response.EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == created.Account.Id);
        user.MedicalWorkerId.Should().Be(created.Account.MedicalWorkerId);
        user.IsActive.Should().Be(!delete);
        (await db.RefreshSessions.CountAsync(item => item.UserId == user.Id && item.RevokedAtUtc == null &&
            item.ExpiresAtUtc > DateTimeOffset.UtcNow)).Should().Be(0);
        var events = await db.AuditEvents.Where(item => item.EventType == nameof(AuditEventType.AccountChanged)).ToArrayAsync();
        events.Where(item => item.MetadataJson.Contains(user.Id.ToString())).Should().Contain(item =>
            JsonDocument.Parse(item.MetadataJson).RootElement.GetProperty("revokedRefreshTokenCount").GetInt32() == 2);
        if (delete)
        {
            using var activate = await admin.PutAsJsonAsync($"/api/accounts/{user.Id}/activity", new SetAccountActiveRequest(true));
            activate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task CreateAccount_AuditFailure_ShouldRollbackUserRoleAndWorkerBinding()
    {
        using var admin = await AdminAsync();
        using var workerResponse = await admin.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Лев", "Откат", "Врач", MedicalWorkerRole.Doctor));
        workerResponse.EnsureSuccessStatusCode();
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var service = new AccountService(db, scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>(),
                TimeProvider.System, new AccountPasswordGenerator(), new ThrowingAuditWriter());
            Func<Task> create = () => service.CreateForWorkerAsync(worker.Id,
                new CreateWorkerAccountRequest("roll." + Guid.NewGuid().ToString("N")[..11]), CancellationToken.None);
            await create.Should().ThrowAsync<InvalidOperationException>();
        }
        await using var verification = factory.Services.CreateAsyncScope();
        (await verification.ServiceProvider.GetRequiredService<HospitalDbContext>().Users
            .AnyAsync(item => item.MedicalWorkerId == worker.Id)).Should().BeFalse();
    }

    private async Task SeedSessionsAsync(Guid accountId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 4; index++)
            db.RefreshSessions.Add(new RefreshSessionDbo
            {
                Id = Guid.NewGuid(), UserId = accountId, FamilyId = Guid.NewGuid(),
                TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                CreatedAtUtc = now.AddHours(-2), ExpiresAtUtc = index == 2 ? now.AddHours(-1) : now.AddHours(1),
                RevokedAtUtc = index == 3 ? now.AddMinutes(-1) : null,
                RevokeReason = index == 3 ? "logout" : null
            });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = factory.CreateCookieClient();
        await CsrfAsync(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin());
        login.EnsureSuccessStatusCode();
        await CsrfAsync(client);
        return client;
    }

    private static async Task LoginAsync(HttpClient client, string name, string password)
    {
        await CsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, password));
        response.EnsureSuccessStatusCode();
        await CsrfAsync(client);
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }

    private static async Task<CreateWorkerAccountResultDto> CreateAsync(HttpClient admin)
    {
        using var workerResponse = await admin.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Лев", "Проверочный", "Врач", MedicalWorkerRole.Doctor));
        workerResponse.EnsureSuccessStatusCode();
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        using var accountResponse = await admin.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("life." + Guid.NewGuid().ToString("N")[..11]));
        accountResponse.EnsureSuccessStatusCode();
        accountResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        return (await accountResponse.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null, AuditActor? actor = null) =>
            throw new InvalidOperationException("Audit failure used to verify transaction rollback.");
    }
}
