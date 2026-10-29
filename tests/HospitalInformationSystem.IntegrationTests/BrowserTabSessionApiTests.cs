using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Api.Middleware;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class BrowserTabSessionApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
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
}
