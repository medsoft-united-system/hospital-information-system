using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AccountModelApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    private const string Password = "Integration.Account-Only.6249!";

    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    public async Task CreateAccount_UsernameBoundaryLengths_ShouldPersistUtcTimestamps(int length)
    {
        using var administrator = await CreateAdminClientAsync();
        var worker = await CreateWorkerAsync(administrator);
        var name = new string('a', length - 3) + "123";
        var before = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();

        using var response = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest(name));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var account = (await response.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!.Account;
        account.UserName.Should().Be(name);
        account.IsActive.Should().BeTrue();
        account.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        account.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(factory.Services.GetRequiredService<TimeProvider>().GetUtcNow());
        account.UpdatedAt.Should().BeOnOrAfter(account.CreatedAt);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == account.Id);
        user.CreatedAtUtc.Should().BeCloseTo(account.CreatedAt, TimeSpan.FromMicroseconds(1));
        user.UpdatedAtUtc.Should().BeCloseTo(account.UpdatedAt, TimeSpan.FromMicroseconds(1));
        user.IsAdministrator.Should().BeFalse();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("русский")]
    [InlineData("bad/name")]
    public async Task CreateAccount_InvalidUsername_ShouldRejectWithoutPartialAccount(string name)
    {
        using var administrator = await CreateAdminClientAsync();
        var worker = await CreateWorkerAsync(administrator);

        using var response = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest(name));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Users.AnyAsync(item => item.MedicalWorkerId == worker.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task CreateUser_DirectIdentityCreation_ShouldUseUsernameValidator()
    {
        using var administrator = await CreateAdminClientAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();

        var result = await users.CreateAsync(new HospitalUser
        {
            Id = Guid.NewGuid(), UserName = "abcdefghijklmnopq"
        }, Password);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(item => item.Code == "InvalidUserNameLength");
    }

    [Fact]
    public async Task CreateAccount_ExistingAdministrator_ShouldRejectSecondAdministrator()
    {
        using var administrator = await CreateAdminClientAsync();

        using var response = await administrator.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("second.admin", Password, ApplicationRoles.Administrator, null));

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Users.CountAsync(item => item.IsAdministrator)).Should().Be(1);
        (await db.Users.AnyAsync(item => item.UserName == "second.admin")).Should().BeFalse();
    }

    [Theory]
    [InlineData(MedicalWorkerRole.Doctor)]
    [InlineData(MedicalWorkerRole.Nurse)]
    [InlineData(MedicalWorkerRole.LaboratoryTechnician)]
    [InlineData(MedicalWorkerRole.Other)]
    public async Task LoginAndGetSession_LinkedWorkerRole_ShouldReturnPersistedMedicalRole(MedicalWorkerRole role)
    {
        using var administrator = await CreateAdminClientAsync();
        var worker = await CreateWorkerAsync(administrator, role);
        var account = await CreateAccountAsync(administrator, worker.Id);
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(account.Account.UserName, account.GeneratedPassword));

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!.Session.MedicalWorkerRole.Should().Be(role);
        var session = (await client.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;
        session.MedicalWorkerRole.Should().Be(role);
    }

    [Fact]
    public async Task DeactivateAccount_ExistingSession_ShouldRevokeCookieAndRejectNewLogin()
    {
        using var administrator = await CreateAdminClientAsync();
        var worker = await CreateWorkerAsync(administrator);
        var account = await CreateAccountAsync(administrator, worker.Id);
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(account.Account.UserName, account.GeneratedPassword));
        login.EnsureSuccessStatusCode();
        (await client.GetAsync("/api/patients")).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var user = await db.Users.SingleAsync(item => item.Id == account.Account.Id);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        (await client.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var fresh = factory.CreateCookieClient();
        await SetCsrfAsync(fresh);
        using var denied = await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest(account.Account.UserName, account.GeneratedPassword));
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fresh.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var listed = (await administrator.GetFromJsonAsync<AccountDetailsDto[]>("/api/accounts"))!;
        listed.Single(item => item.Id == account.Account.Id).IsActive.Should().BeFalse();
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin());
        login.EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        return client;
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RequestToken);
    }

    private static async Task<MedicalWorkerDto> CreateWorkerAsync(HttpClient administrator,
        MedicalWorkerRole role = MedicalWorkerRole.Doctor)
    {
        using var response = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Л", "Т", "Врач", Role: role));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
    }

    private static async Task<CreateWorkerAccountResultDto> CreateAccountAsync(HttpClient administrator, Guid workerId)
    {
        using var response = await administrator.PostAsJsonAsync($"/api/medical-workers/{workerId}/account", new CreateWorkerAccountRequest(
            "sec." + Guid.NewGuid().ToString("N")[..12]));
        response.StatusCode.Should().Be(HttpStatusCode.Created, "API response: {0}",
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
    }
}
