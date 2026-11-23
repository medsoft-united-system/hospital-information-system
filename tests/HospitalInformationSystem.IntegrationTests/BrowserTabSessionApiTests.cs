using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Api.Middleware;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.MedicalWorkers;
using HospitalInformationSystem.Models.Patients;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MedicalWorkerRole = HospitalInformationSystem.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class BrowserTabSessionApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Theory]
    [InlineData("logout")]
    [InlineData("login")]
    [InlineData("admin-login")]
    public async Task AuthenticationTransition_ExpiredAccessWithCachedCsrf_ShouldRenewCsrfAndPreserveOtherTab(string transition)
    {
        var account = await AccountAsync();
        var cookies = new CookieContainer();
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var firstState = new SessionState();
        var firstCsrf = new AntiforgeryTokenStore();
        var firstOperations = new AuthenticationOperationCoordinator();
        var secondState = new SessionState();
        var secondCsrf = new AntiforgeryTokenStore();
        var secondOperations = new AuthenticationOperationCoordinator();
        using var first = BrowserClient(cookies, firstId, firstState, firstCsrf, firstOperations);
        using var second = BrowserClient(cookies, secondId, secondState, secondCsrf, secondOperations);
        var firstApi = new SessionApiClient(first, firstState, firstCsrf, firstOperations);
        var secondApi = new SessionApiClient(second, secondState, secondCsrf, secondOperations);
        var login = new LoginRequest(account.Account.UserName, account.GeneratedPassword);
        await firstApi.LoginAsync(login, CancellationToken.None);
        await secondApi.LoginAsync(login, CancellationToken.None);
        using var patient = await first.PostAsJsonAsync("/api/patients",
            new SavePatientRequest("Проверка", "Сессии", null, new DateOnly(1990, 1, 1), AdministrativeSex.Male, null));
        patient.EnsureSuccessStatusCode();
        firstCsrf.RequestToken.Should().NotBeNull();
        var firstRefresh = cookies.GetCookies(new Uri("https://localhost"))["MedSoft.Refresh." + firstId]!.Value;
        var secondRefresh = cookies.GetCookies(new Uri("https://localhost"))["MedSoft.Refresh." + secondId]!.Value;
        factory.AdvanceTime(TimeSpan.FromMinutes(16));

        using (var stale = TabClient(cookies, firstId))
        {
            stale.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstState.AccessToken);
            stale.DefaultRequestHeaders.Add("X-CSRF-TOKEN", firstCsrf.RequestToken);
            using var rejected = transition switch
            {
                "logout" => await stale.PostAsync("/api/auth/logout", null),
                "login" => await stale.PostAsJsonAsync("/api/auth/login", login),
                _ => await stale.PostAsJsonAsync("/api/auth/admin/login", new BeginAdministratorLoginRequest(
                    SecureHospitalApiFactory.AdminUserName, SecureHospitalApiFactory.AdminPassword))
            };
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        switch (transition)
        {
            case "logout":
                await firstApi.LogoutAsync(CancellationToken.None);
                firstState.Current.Should().BeNull();
                firstState.AccessToken.Should().BeNull();
                cookies.GetCookies(new Uri("https://localhost"))["MedSoft.Refresh." + firstId].Should().BeNull();
                break;
            case "login":
                await firstApi.LoginAsync(login, CancellationToken.None);
                firstState.Current!.Id.Should().Be(account.Account.Id);
                using (var current = await first.GetAsync("/api/patients")) current.EnsureSuccessStatusCode();
                break;
            default:
                await firstApi.BeginAdministratorLoginAsync(new BeginAdministratorLoginRequest(
                    SecureHospitalApiFactory.AdminUserName, SecureHospitalApiFactory.AdminPassword), CancellationToken.None);
                firstState.Current.Should().BeNull();
                await firstApi.ConfirmAdministratorLoginAsync(new ConfirmAdministratorLoginRequest(
                    factory.AdministratorLogin().TotpCode!), CancellationToken.None);
                firstState.Current!.Role.Should().Be(ApplicationRoles.Administrator);
                using (var current = await first.GetAsync("/api/accounts")) current.EnsureSuccessStatusCode();
                break;
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var firstHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(firstRefresh)));
            var secondHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secondRefresh)));
            (await db.RefreshSessions.SingleAsync(row => row.TokenHash == firstHash)).RevokedAtUtc.Should().NotBeNull();
            (await db.RefreshSessions.SingleAsync(row => row.TokenHash == secondHash)).RevokedAtUtc.Should().BeNull();
        }
        using var otherTab = await second.GetAsync("/api/patients");
        otherTab.EnsureSuccessStatusCode();
        secondState.Current!.Id.Should().Be(account.Account.Id);
    }

    [Fact]
    public async Task DifferentAccounts_SharedCookieJar_ShouldKeepLoginRefreshAndLogoutIndependent()
    {
        var account = await AccountAsync();
        var cookies = new CookieContainer();
        var firstTabId = Guid.NewGuid().ToString("N");
        var secondTabId = Guid.NewGuid().ToString("N");
        using var first = TabClient(cookies, firstTabId);
        using var second = TabClient(cookies, secondTabId);
        var administrator = await LoginAsync(first, factory.AdministratorLogin());
        var worker = await LoginAsync(second, new LoginRequest(account.Account.UserName, account.GeneratedPassword));

        (await first.GetAsync("/api/medical-workers")).EnsureSuccessStatusCode();
        (await second.GetAsync("/api/patients")).EnsureSuccessStatusCode();
        cookies.GetCookies(new Uri("https://localhost"))["MedSoft.Refresh." + firstTabId].Should().NotBeNull();
        cookies.GetCookies(new Uri("https://localhost"))["MedSoft.Refresh." + secondTabId].Should().NotBeNull();

        await CsrfAsync(first);
        await CsrfAsync(second);
        var refreshed = await Task.WhenAll(first.PostAsync("/api/auth/refresh", null), second.PostAsync("/api/auth/refresh", null));
        foreach (var response in refreshed) response.EnsureSuccessStatusCode();
        var updatedAdministrator = (await refreshed[0].Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        var updatedWorker = (await refreshed[1].Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        updatedAdministrator.Session.Id.Should().Be(administrator.Session.Id);
        updatedWorker.Session.Id.Should().Be(worker.Session.Id);
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", updatedAdministrator.AccessToken);
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", updatedWorker.AccessToken);
        foreach (var response in refreshed) response.Dispose();

        await CsrfAsync(first);
        (await first.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await first.GetAsync("/api/medical-workers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await second.GetAsync("/api/patients")).EnsureSuccessStatusCode();
        await CsrfAsync(second);
        using var continued = await second.PostAsync("/api/auth/refresh", null);
        continued.EnsureSuccessStatusCode();
        (await continued.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!.Session.Id.Should().Be(worker.Session.Id);
    }

    [Fact]
    public async Task SameAccount_SeparateTabs_ShouldCreateIndependentFamiliesAndKeepOtherAccessAfterRotation()
    {
        var account = await AccountAsync();
        var cookies = new CookieContainer();
        using var first = TabClient(cookies, Guid.NewGuid().ToString("N"));
        using var second = TabClient(cookies, Guid.NewGuid().ToString("N"));
        var request = new LoginRequest(account.Account.UserName, account.GeneratedPassword);
        await LoginAsync(first, request);
        await LoginAsync(second, request);

        await CsrfAsync(first);
        using var response = await first.PostAsync("/api/auth/refresh", null);
        response.EnsureSuccessStatusCode();
        (await second.GetAsync("/api/patients")).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().RefreshSessions
            .Where(item => item.UserId == account.Account.Id && item.RevokedAtUtc == null).ToArrayAsync();
        sessions.Select(item => item.FamilyId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task NewTab_WithOnlyOtherTabAndLegacyCookies_ShouldNotRestoreOrRevokeTheirSessions()
    {
        var account = await AccountAsync();
        var cookies = new CookieContainer();
        using var legacy = TabClient(cookies, null);
        using var first = TabClient(cookies, Guid.NewGuid().ToString("N"));
        using var empty = TabClient(cookies, Guid.NewGuid().ToString("N"));
        var request = new LoginRequest(account.Account.UserName, account.GeneratedPassword);
        await LoginAsync(legacy, request);
        await LoginAsync(first, request);
        await CsrfAsync(empty);

        (await empty.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await empty.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await legacy.GetAsync("/api/patients")).EnsureSuccessStatusCode();
        (await first.GetAsync("/api/patients")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AdministratorChallenges_SharedCookieJar_ShouldRemainIndependentWhenAnotherTabLogsOut()
    {
        var cookies = new CookieContainer();
        using var first = TabClient(cookies, Guid.NewGuid().ToString("N"));
        using var second = TabClient(cookies, Guid.NewGuid().ToString("N"));
        await BeginAdministratorLoginAsync(first);
        await BeginAdministratorLoginAsync(second);
        (await first.GetAsync("/api/auth/admin/challenge")).EnsureSuccessStatusCode();
        (await second.GetAsync("/api/auth/admin/challenge")).EnsureSuccessStatusCode();

        await CsrfAsync(second);
        (await second.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await second.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await first.GetAsync("/api/auth/admin/challenge")).EnsureSuccessStatusCode();
        await CsrfAsync(first);
        using var confirmed = await first.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));
        confirmed.EnsureSuccessStatusCode();
        (await confirmed.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!.Session.AccountRole
            .Should().Be(ApplicationRoles.Administrator);
    }

    [Fact]
    public async Task FirstUse_ConcurrentCsrfCreation_ShouldAllowBothTabsToAuthenticate()
    {
        var account = await AccountAsync();
        var cookies = new CookieContainer();
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        using var first = TabClient(cookies, firstId);
        using var second = TabClient(cookies, secondId);
        await Task.WhenAll(CsrfAsync(first), CsrfAsync(second));
        var saved = cookies.GetCookies(new Uri("https://localhost"));
        saved["MedSoft.Antiforgery." + firstId].Should().NotBeNull();
        saved["MedSoft.Antiforgery." + secondId].Should().NotBeNull();
        saved["MedSoft.Antiforgery"].Should().BeNull();
        var request = new LoginRequest(account.Account.UserName, account.GeneratedPassword);

        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/auth/login", request),
            second.PostAsJsonAsync("/api/auth/login", request));

        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();
            response.Dispose();
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("../0123456789abcdef0123456789ab")]
    public async Task InvalidTabIdentifier_ShouldRejectBeforeAuthenticationWithoutChangingCookies(string identifier)
    {
        using var client = TabClient(new CookieContainer(), identifier);

        using var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task ExplicitEmptyTabHeader_ShouldRejectBeforeNextMiddlewareWithoutChangingCookies()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers[BrowserTabSession.HeaderName] = "";
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = new BrowserTabSessionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, scope.ServiceProvider.GetRequiredService<IOptions<AntiforgeryOptions>>());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.Headers.ContainsKey("Set-Cookie").Should().BeFalse();
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task MultipleTabIdentifiers_ShouldRejectBeforeAuthentication()
    {
        using var client = TabClient(new CookieContainer(), null);
        client.DefaultRequestHeaders.TryAddWithoutValidation(BrowserTabSession.HeaderName,
            new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") });

        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private HttpClient TabClient(CookieContainer cookies, string? tabId)
    {
        var client = factory.CreateDefaultClient(new Uri("https://localhost"), new SharedCookiesHandler(cookies));
        if (tabId is not null) client.DefaultRequestHeaders.TryAddWithoutValidation(BrowserTabSession.HeaderName, tabId);
        return client;
    }

    private HttpClient BrowserClient(CookieContainer cookies, string tabId, SessionState session,
        AntiforgeryTokenStore csrf, AuthenticationOperationCoordinator operations)
    {
        var origin = new Uri("https://localhost");
        return factory.CreateDefaultClient(origin,
            new BrowserSessionHandler(session, csrf, operations, origin, new TabSession(tabId)),
            new SharedCookiesHandler(cookies));
    }

    private static async Task<AuthenticationTokensDto> LoginAsync(HttpClient client, LoginRequest request)
    {
        await CsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", request);
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return tokens;
    }

    private static async Task BeginAdministratorLoginAsync(HttpClient client)
    {
        await CsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/admin/login",
            new BeginAdministratorLoginRequest(SecureHospitalApiFactory.AdminUserName, SecureHospitalApiFactory.AdminPassword));
        response.EnsureSuccessStatusCode();
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }

    private async Task<CreateWorkerAccountResultDto> AccountAsync()
    {
        using var administrator = factory.CreateCookieClient();
        await CsrfAsync(administrator);
        using var login = await administrator.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin());
        login.EnsureSuccessStatusCode();
        await CsrfAsync(administrator);
        using var workerResponse = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Лев", "Проверочный", "Врач", MedicalWorkerRole.Doctor));
        workerResponse.EnsureSuccessStatusCode();
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        using var accountResponse = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("tab." + Guid.NewGuid().ToString("N")[..12]));
        accountResponse.EnsureSuccessStatusCode();
        return (await accountResponse.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
    }

    private sealed class SharedCookiesHandler(CookieContainer cookies) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var header = cookies.GetCookieHeader(uri);
            if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) cookies.SetCookies(uri, value);
            return response;
        }
    }

    private sealed class TabSession(string id) : IBrowserTabSession
    {
        public Task<string> GetIdAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(id);
        }
    }
}
