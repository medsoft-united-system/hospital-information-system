using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkerRole = HospitalInformationSystem.Domain.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAccountsApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task CreateAndRead_ShouldReturnKeyOnlyOnceAndKeepServiceSeparateFromHumanAccounts()
    {
        using var client = await AdministratorAsync();
        var admin = (await client.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
        var name = UniqueName();

        using var response = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(" " + name + " "));

        var created = await ReadAsync<ServiceAccountKeyResultDto>(response, HttpStatusCode.Created);
        created.Account.Name.Should().Be(name);
        created.Account.Role.Should().Be("LIS");
        created.Account.IsActive.Should().BeTrue();
        created.Account.Version.Should().BeGreaterThan(0);
        created.Account.KeyPrefix.Should().Be($"msa_{created.Account.Id:N}.");
        created.Account.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        created.Account.UpdatedAtUtc.Should().Be(created.Account.CreatedAtUtc);
        created.RevokedSessionCount.Should().Be(0);
        ValidKey(created.Account.Id, created.ApiKey).Should().BeTrue("the generated API key must follow C2");
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().EndWith($"/api/service-accounts/{created.Account.Id}");
        using var single = await client.GetAsync(response.Headers.Location);
        var singleJson = await single.Content.ReadAsStringAsync();
        single.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(singleJson)) AssertPublicShape(document.RootElement);
        singleJson.Contains(created.ApiKey, StringComparison.Ordinal).Should().BeFalse();
        (await single.Content.ReadFromJsonAsync<ServiceAccountDto>()).Should().BeEquivalentTo(created.Account);
        using var list = await client.GetAsync("/api/service-accounts");
        var listJson = await list.Content.ReadAsStringAsync();
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(listJson))
            foreach (var item in document.RootElement.EnumerateArray()) AssertPublicShape(item);
        listJson.Contains(created.ApiKey, StringComparison.Ordinal).Should().BeFalse();
        var humanAccounts = (await client.GetFromJsonAsync<AccountDetailsDto[]>("/api/accounts"))!;
        humanAccounts.Should().NotContain(account => account.Id == created.Account.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var saved = await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
        saved.NormalizedName.Should().Be(name.ToUpperInvariant());
        saved.AuthenticationVersion.Should().Be(1);
        saved.ApiKeyHash.Should().Equal(HashKey(created.ApiKey));
        (await db.Users.AnyAsync(account => account.Id == created.Account.Id)).Should().BeFalse();
        var audit = (await AuditsAsync(db, created.Account.Id)).Should().ContainSingle().Subject;
        audit.EventType.Should().Be(nameof(AuditEventType.AccountCreated));
        audit.ActorKind.Should().Be("user");
        audit.ActorUserId.Should().Be(admin.Id);
        audit.ActorServiceAccountId.Should().BeNull();
        audit.RequestId.Should().NotBeNullOrWhiteSpace();
        (audit.MetadataJson.Contains(created.ApiKey, StringComparison.Ordinal) || audit.Message.Contains(created.ApiKey, StringComparison.Ordinal))
            .Should().BeFalse("security audit must not contain the generated credential");
    }

    [Fact]
    public async Task Rename_ShouldPreserveKeyAndAuthenticationVersionAndRejectStaleConfirmations()
    {
        using var client = await AdministratorAsync();
        var created = await CreateAsync(client);
        var renamedName = UniqueName();

        using var response = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}",
            new UpdateServiceAccountRequest(" " + renamedName + " ", created.Account.Version));

        var renamed = await ReadAsync<ServiceAccountDto>(response);
        renamed.Name.Should().Be(renamedName);
        renamed.Version.Should().NotBe(created.Account.Version);
        renamed.CreatedAtUtc.Should().Be(created.Account.CreatedAtUtc);
        using var staleName = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}",
            new UpdateServiceAccountRequest(UniqueName(), created.Account.Version));
        staleName.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var staleRotation = await client.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key",
            new RotateServiceAccountKeyRequest(created.Account.Version));
        staleRotation.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var staleActivity = await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}/active",
            new SetServiceAccountActiveRequest(false, created.Account.Version));
        staleActivity.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<ServiceAccountDto>($"/api/service-accounts/{created.Account.Id}"))
            .Should().BeEquivalentTo(renamed);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var stored = await db.ServiceAccounts.AsNoTracking().SingleAsync(account => account.Id == created.Account.Id);
        stored.ApiKeyHash.Should().Equal(HashKey(created.ApiKey));
        stored.AuthenticationVersion.Should().Be(1);
        (await AuditsAsync(db, created.Account.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task DuplicateName_ShouldConflictIgnoringCaseAndPreserveBothExistingAccounts()
    {
        using var client = await AdministratorAsync();
        var first = await CreateAsync(client);
        var second = await CreateAsync(client);
        using var duplicateCreate = await client.PostAsJsonAsync("/api/service-accounts",
            new CreateServiceAccountRequest(first.Account.Name.ToUpperInvariant()));
        duplicateCreate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var duplicateRename = await client.PutAsJsonAsync($"/api/service-accounts/{second.Account.Id}",
            new UpdateServiceAccountRequest(first.Account.Name.ToUpperInvariant(), second.Account.Version));
        duplicateRename.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<ServiceAccountDto>($"/api/service-accounts/{second.Account.Id}"))
            .Should().BeEquivalentTo(second.Account);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.ServiceAccounts.CountAsync(account => account.NormalizedName == first.Account.Name.ToUpperInvariant()))
            .Should().Be(1);
        (await AuditsAsync(db, second.Account.Id)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    [InlineData("lis\0gateway")]
    [InlineData("lis\ngateway")]
    [InlineData("lis\tgateway")]
    public async Task InvalidName_ShouldReturn400WithoutCreatingAnAccount(string name)
    {
        using var client = await AdministratorAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var before = await db.ServiceAccounts.CountAsync();
        using var response = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await db.ServiceAccounts.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task UnicodeNameBoundary_ShouldCountScalarsAndRejectTheSixtyFifthScalar()
    {
        using var client = await AdministratorAsync();
        var name = string.Concat(Enumerable.Repeat("🧪", 64));
        using var response = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(name));
        var created = await ReadAsync<ServiceAccountKeyResultDto>(response, HttpStatusCode.Created);
        created.Account.Name.Should().Be(name);
        using var tooLong = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(name + "а"));
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("PUT", "", "{\"name\":\"valid-name\"}")]
    [InlineData("PUT", "", "{\"name\":\"valid-name\",\"version\":0}")]
    [InlineData("PUT", "/active", "{\"version\":1}")]
    [InlineData("PUT", "/active", "{\"isActive\":false}")]
    [InlineData("POST", "/rotate-key", "{}")]
    [InlineData("POST", "/rotate-key", "{\"version\":0}")]
    public async Task MissingRequiredMutationField_ShouldReturn400AndLeaveAccountUnchanged(string method, string suffix, string body)
    {
        using var client = await AdministratorAsync();
        var created = await CreateAsync(client);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/service-accounts/{created.Account.Id}{suffix}")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<ServiceAccountDto>($"/api/service-accounts/{created.Account.Id}"))
            .Should().BeEquivalentTo(created.Account);
    }

    [Fact]
    public async Task MissingAndEmptyAccountId_ShouldReturn404And400WithoutCreatingAnything()
    {
        using var client = await AdministratorAsync();
        var missing = Guid.NewGuid();
        (await client.GetAsync($"/api/service-accounts/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/api/service-accounts/{missing}", new UpdateServiceAccountRequest(UniqueName(), 1)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/api/service-accounts/{missing}/active", new SetServiceAccountActiveRequest(false, 1)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/service-accounts/{missing}/rotate-key", new RotateServiceAccountKeyRequest(1)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/service-accounts/{Guid.Empty}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/service-accounts/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdministratorMutations_ShouldAlwaysRequireHumanCsrf()
    {
        using var client = await AdministratorAsync();
        var created = await CreateAsync(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        (await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(UniqueName())))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}", new UpdateServiceAccountRequest(UniqueName(), created.Account.Version)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync($"/api/service-accounts/{created.Account.Id}/active", new SetServiceAccountActiveRequest(false, created.Account.Version)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/service-accounts/{created.Account.Id}/rotate-key", new RotateServiceAccountKeyRequest(created.Account.Version)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<ServiceAccountDto>($"/api/service-accounts/{created.Account.Id}"))
            .Should().BeEquivalentTo(created.Account);
    }

    [Fact]
    public async Task Anonymous_ShouldNotReadOrMutateEvenWithValidCsrf()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        var id = Guid.NewGuid();
        (await client.GetAsync("/api/service-accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"/api/service-accounts/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(UniqueName())))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PutAsJsonAsync($"/api/service-accounts/{id}", new UpdateServiceAccountRequest(UniqueName(), 1)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PutAsJsonAsync($"/api/service-accounts/{id}/active", new SetServiceAccountActiveRequest(false, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync($"/api/service-accounts/{id}/rotate-key", new RotateServiceAccountKeyRequest(1)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(WorkerRole.Doctor)]
    [InlineData(WorkerRole.Nurse)]
    [InlineData(WorkerRole.LaboratoryTechnician)]
    [InlineData(WorkerRole.Other)]
    public async Task MedicalRoles_ShouldNeverReadOrManageServiceCredentials(WorkerRole role)
    {
        using var client = await ClinicianAsync(role);
        var id = Guid.NewGuid();
        (await client.GetAsync("/api/service-accounts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/service-accounts/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(UniqueName())))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/service-accounts/{id}", new UpdateServiceAccountRequest(UniqueName(), 1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/service-accounts/{id}/active", new SetServiceAccountActiveRequest(false, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"/api/service-accounts/{id}/rotate-key", new RotateServiceAccountKeyRequest(1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> AdministratorAsync()
    {
        var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin())).EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        return client;
    }

    private async Task<HttpClient> ClinicianAsync(WorkerRole role)
    {
        var userName = "srv." + Guid.NewGuid().ToString("N")[..10];
        const string password = "Service.Matrix-6249!";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            var worker = new MedicalWorkerDbo
            {
                Id = Guid.NewGuid(), FirstName = "Проверка", LastName = "Сервисных", Position = "Сотрудник",
                Role = role, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.MedicalWorkers.Add(worker);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
                new CreateAccountRequest(userName, password, ApplicationRoles.MedicalWorker, worker.Id), CancellationToken.None);
        }
        var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password))).EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        return client;
    }

    internal static async Task SetCsrfAsync(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RequestToken);
    }

    internal static async Task<ServiceAccountKeyResultDto> CreateAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest(UniqueName()));
        return await ReadAsync<ServiceAccountKeyResultDto>(response, HttpStatusCode.Created);
    }

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    internal static string UniqueName() => "lis-" + Guid.NewGuid().ToString("N");

    internal static byte[] HashKey(string key)
    {
        var secret = key[(key.IndexOf('.') + 1)..].Replace('-', '+').Replace('_', '/') + "=";
        return SHA256.HashData(Convert.FromBase64String(secret));
    }

    private static bool ValidKey(Guid id, string key) =>
        Regex.IsMatch(key, $"\\Amsa_{id:N}\\.[A-Za-z0-9_-]{{43}}\\z", RegexOptions.CultureInvariant);

    private static void AssertPublicShape(JsonElement value)
    {
        var fields = value.EnumerateObject().Select(property => property.Name).ToArray();
        fields.Should().NotContain(["apiKey", "apiKeyHash", "normalizedName", "authenticationVersion"]);
    }

    internal static async Task<IReadOnlyList<AuditEventDbo>> AuditsAsync(HospitalDbContext db, Guid id)
    {
        var entries = await db.AuditEvents.AsNoTracking().Where(entry => entry.EventType == nameof(AuditEventType.AccountCreated) ||
            entry.EventType == nameof(AuditEventType.AccountChanged)).ToArrayAsync();
        return entries.Where(entry =>
        {
            using var metadata = JsonDocument.Parse(entry.MetadataJson);
            return metadata.RootElement.TryGetProperty("serviceAccountId", out var value) && value.GetGuid() == id;
        }).ToArray();
    }
}
