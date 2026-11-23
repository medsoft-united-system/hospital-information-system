using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LoginPortalApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Theory]
    [InlineData(LoginPortal.Administrator, HttpStatusCode.OK)]
    [InlineData(LoginPortal.MedicalWorker, HttpStatusCode.Unauthorized)]
    public async Task Login_SelectedPortal_ShouldPreserveActualAdministratorRole(
        LoginPortal portal, HttpStatusCode expected)
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var response = await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin() with { Portal = portal });

        response.StatusCode.Should().Be(expected);
        using var session = await client.GetAsync("/api/auth/session");
        session.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
            (await session.Content.ReadFromJsonAsync<SessionDto>())!.Role.Should().Be(ApplicationRoles.Administrator);
        else
            AssertNoRefreshCookie(response);
    }

    [Fact]
    public async Task Login_MedicalWorkerAtAdministratorPortal_ShouldRejectValidCredentials()
    {
        using var administrator = factory.CreateCookieClient();
        await SetCsrfAsync(administrator);
        using var login = await administrator.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin());
        login.EnsureSuccessStatusCode();
        await SetCsrfAsync(administrator);
        using var createdWorker = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Алексей", "Проверочный", "Врач"));
        createdWorker.EnsureSuccessStatusCode();
        var worker = (await createdWorker.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        var name = "portal." + Guid.NewGuid().ToString("N")[..9];
        using var createdAccount = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest(name));
        createdAccount.EnsureSuccessStatusCode();
        var password = (await createdAccount.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!.GeneratedPassword;
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var rejected = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(name, password, Portal: LoginPortal.Administrator));

        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(rejected);
        using var anonymous = await client.GetAsync("/api/auth/session");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var accepted = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(name, password, Portal: LoginPortal.MedicalWorker));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await accepted.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!.Session.AccountRole.Should().Be(ApplicationRoles.MedicalWorker);
    }

    [Fact]
    public async Task Login_IncorrectAdministratorTotp_ShouldRejectUnverifiedCode()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var response = await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin() with { TotpCode = "invalid" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(response);
        using var session = await client.GetAsync("/api/auth/session");
        session.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RequestToken);
    }

    private static void AssertNoRefreshCookie(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Should().NotContain(cookie => cookie.Contains("MedSoft.Refresh=", StringComparison.Ordinal));
    }
}
