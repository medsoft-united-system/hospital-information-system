using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.MedicalWorkers;
using HospitalInformationSystem.Models.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using MedicalWorkerRole = HospitalInformationSystem.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AuthenticationTokenApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task ReadPatients_RefreshCookieWithoutBearer_ShouldRequireAccessToken()
    {
        var account = await AccountAsync();
        using var browser = RawClient();
        using var login = await LoginAsync(browser, account);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;

        (await browser.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        (await browser.GetAsync("/api/patients")).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadPatients_TamperedJwtPayloadOrSignature_ShouldRejectAtBearerBoundary(bool tamperSignature)
    {
        var account = await AccountAsync();
        using var browser = RawClient();
        using var login = await LoginAsync(browser, account);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        var parts = tokens.AccessToken.Split('.');
        if (tamperSignature)
            parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];
        else
        {
            var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!;
            payload[SecurityTokenClaimTypes.AccountRole] = ApplicationRoles.Administrator;
            parts[1] = Base64UrlEncoder.Encode(payload.ToJsonString());
        }
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", string.Join('.', parts));

        (await browser.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshSession_ValidCookieAndCsrf_ShouldRotateCookieAndRevokePreviousAccess()
    {
        var account = await AccountAsync();
        using var browser = RawClient();
        using var login = await LoginAsync(browser, account);
        var initial = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        var oldCookie = RefreshCookie(login);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", initial.AccessToken);
        await CsrfAsync(browser);

        using var refreshed = await browser.PostAsync("/api/auth/refresh", null);

        refreshed.EnsureSuccessStatusCode();
        var current = (await refreshed.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        current.AccessToken.Should().NotBe(initial.AccessToken);
        RefreshCookie(refreshed).Should().NotBe(oldCookie);
        current.Session.Id.Should().Be(account.Account.Id);
        (await browser.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        (await browser.GetAsync("/api/patients")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_FailedReplacementLogin_ShouldRevokePreviousFamilyAndPreventRefreshRestoration()
    {
        var account = await AccountAsync();
        using var browser = RawClient();
        using var login = await LoginAsync(browser, account);
        var previous = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        var oldCookie = RefreshCookie(login);
        await CsrfAsync(browser);

        using var rejected = await browser.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(account.Account.UserName, "WrongGeneratedPassword123"));

        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        rejected.Headers.GetValues("Set-Cookie").Should().Contain(value =>
            value.StartsWith("MedSoft.Refresh=;", StringComparison.Ordinal));
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", previous.AccessToken);
        (await browser.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        browser.DefaultRequestHeaders.Authorization = null;
        await CsrfAsync(browser);
        (await browser.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var replay = RawClient();
        await CsrfAsync(replay);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.TryAddWithoutValidation("Cookie", oldCookie);
        (await replay.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_BearerWithoutRefreshCookie_ShouldRevokeAuthenticatedFamily()
    {
        var account = await AccountAsync();
        using var original = RawClient();
        using var login = await LoginAsync(original, account);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        using var bearerOnly = RawClient();
        bearerOnly.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        await CsrfAsync(bearerOnly);

        (await bearerOnly.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await bearerOnly.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await CsrfAsync(original);
        (await original.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_ExpiredOrRevokedRefreshWithoutBearer_ShouldKeepUnverifiedActorAnonymous(bool expired)
    {
        var account = await AccountAsync();
        using var browser = RawClient();
        using var login = await LoginAsync(browser, account);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var row = await db.RefreshSessions.SingleAsync(item => item.UserId == account.Account.Id);
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            row.CreatedAtUtc = now.AddHours(-2);
            row.ExpiresAtUtc = expired ? now.AddHours(-1) : now.AddHours(1);
            row.RevokedAtUtc = expired ? null : now.AddMinutes(-1);
            row.RevokeReason = expired ? null : "password-reset";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await CsrfAsync(browser);

        (await browser.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var verification = factory.Services.CreateAsyncScope();
        var events = await verification.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents
            .Where(item => item.EventType == nameof(AuditEventType.UserLogout)).ToArrayAsync();
        var logged = events.Single(item => item.MetadataJson.Contains(account.Account.Id.ToString()));
        logged.ActorKind.Should().Be("anonymous");
        logged.ActorUserId.Should().BeNull();
        using var metadata = JsonDocument.Parse(logged.MetadataJson);
        metadata.RootElement.GetProperty("accountId").GetGuid().Should().Be(account.Account.Id);
    }

    private HttpClient RawClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
    });

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient browser, CreateWorkerAccountResultDto account)
    {
        await CsrfAsync(browser);
        var login = await browser.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(account.Account.UserName, account.GeneratedPassword));
        login.EnsureSuccessStatusCode();
        return login;
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }

    private async Task<CreateWorkerAccountResultDto> AccountAsync()
    {
        using var admin = factory.CreateCookieClient();
        await CsrfAsync(admin);
        using var login = await admin.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin());
        login.EnsureSuccessStatusCode();
        await CsrfAsync(admin);
        using var workerResponse = await admin.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Лев", "Проверочный", "Врач", MedicalWorkerRole.Doctor));
        workerResponse.EnsureSuccessStatusCode();
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        using var accountResponse = await admin.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("http." + Guid.NewGuid().ToString("N")[..11]));
        accountResponse.EnsureSuccessStatusCode();
        return (await accountResponse.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
    }

    private static string RefreshCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(cookie => cookie.StartsWith("MedSoft.Refresh=", StringComparison.Ordinal)).Split(';')[0];
}
