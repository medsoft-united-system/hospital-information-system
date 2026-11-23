using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AdministratorLoginApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task Begin_ValidPassword_ShouldCreateProtectedChallengeWithoutAuthenticatedSession()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var response = await BeginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var challenge = (await response.Content.ReadFromJsonAsync<AdministratorLoginChallengeDto>())!;
        challenge.UserName.Should().Be(SecureHospitalApiFactory.AdminUserName);
        challenge.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("accessToken", out _).Should().BeFalse();
        var cookie = ChallengeCookie(response);
        cookie.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=lax");
        cookie.Should().NotContain(SecureHospitalApiFactory.AdminPassword).And.NotContain(challenge.UserName);
        AssertNoRefreshCookie(response);
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Begin_IncorrectPassword_ShouldRejectWithoutChallenge()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var response = await client.PostAsJsonAsync("/api/auth/admin/login",
            new BeginAdministratorLoginRequest(SecureHospitalApiFactory.AdminUserName, "Wrong-Password.684!"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(response);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_ValidChallengeAndCode_ShouldIssueSessionAndConsumeChallenge()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        tokens.Session.AccountRole.Should().Be(ApplicationRoles.Administrator);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_IncorrectCode_ShouldAllowRetryWithoutRepeatingPassword()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();

        using var rejected = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest("invalid"));

        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(rejected);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var accepted = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Confirm_FiveIncorrectCodes_ShouldExpireChallenge()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var response = await client.PostAsJsonAsync("/api/auth/admin/confirm",
                    new ConfirmAdministratorLoginRequest("invalid"));
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally { factory.AdvanceTime(TimeSpan.FromMinutes(6)); }
    }

    [Fact]
    public async Task Challenge_TamperedCookie_ShouldRejectWithoutServerError()
    {
        using var client = await CreateReplayClientAsync("MedSoft.AdministratorLogin=not-a-protected-challenge");

        using var response = await client.GetAsync("/api/auth/admin/challenge");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_WithoutPasswordChallenge_ShouldRejectEvenCorrectCode()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);

        using var response = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(response);
    }

    [Fact]
    public async Task Confirm_ExpiredChallenge_ShouldRequirePasswordAgain()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();
        factory.AdvanceTime(TimeSpan.FromMinutes(6));

        using var response = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(response);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_ConsumedCookieReplayedWithNewCode_ShouldReject()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();
        var cookie = ChallengeCookie(begin).Split(';')[0];
        using var accepted = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));
        accepted.EnsureSuccessStatusCode();
        using var replay = await CreateReplayClientAsync(cookie);

        using var response = await replay.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(response);
    }

    [Fact]
    public async Task Confirm_ConcurrentRequestsWithSameCookie_ShouldIssueOnlyOneSession()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();
        var request = new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!);

        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/auth/admin/confirm", request),
            client.PostAsJsonAsync("/api/auth/admin/confirm", request));

        try
        {
            responses.Select(response => response.StatusCode).Should()
                .BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Begin_ReplacingPendingChallenge_ShouldInvalidatePreviousCookie()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var first = await BeginAsync(client);
        first.EnsureSuccessStatusCode();
        var cookie = ChallengeCookie(first).Split(';')[0];
        using var second = await BeginAsync(client);
        second.EnsureSuccessStatusCode();
        using var replay = await CreateReplayClientAsync(cookie);

        using var response = await replay.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Confirm_ChangedSecurityStamp_ShouldInvalidatePasswordProof()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<HospitalUser>>();
            var user = (await users.FindByNameAsync(SecureHospitalApiFactory.AdminUserName))!;
            (await users.UpdateSecurityStampAsync(user)).Succeeded.Should().BeTrue();
        }

        using var response = await client.PostAsJsonAsync("/api/auth/admin/confirm",
            new ConfirmAdministratorLoginRequest(factory.AdministratorLogin().TotpCode!));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoRefreshCookie(response);
    }

    [Fact]
    public async Task Logout_PendingChallenge_ShouldCancelPasswordProof()
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        using var begin = await BeginAsync(client);
        begin.EnsureSuccessStatusCode();

        using var logout = await client.PostAsJsonAsync("/api/auth/logout", new { });

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/admin/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/auth/admin/login")]
    [InlineData("/api/auth/admin/confirm")]
    public async Task AdministratorLogin_MissingCsrf_ShouldAuditAnonymousRejection(string path)
    {
        using var client = factory.CreateCookieClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var before = await db.AuditEvents.Select(entry => entry.EventId).ToArrayAsync();

        using var response = await client.PostAsJsonAsync(path, new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(item => !before.Contains(item.EventId));
        entry.EventType.Should().Be(nameof(AuditEventType.LoginFailed));
        entry.ActorKind.Should().Be("anonymous");
        entry.MetadataJson.Should().Contain("csrf-rejected");
    }

    private static Task<HttpResponseMessage> BeginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/admin/login",
            new BeginAdministratorLoginRequest(SecureHospitalApiFactory.AdminUserName, SecureHospitalApiFactory.AdminPassword));

    private async Task<HttpClient> CreateReplayClientAsync(string challengeCookie)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        using var response = await client.GetAsync("/api/auth/csrf");
        var token = (await response.Content.ReadFromJsonAsync<AntiforgeryTokenDto>())!;
        var csrfCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("MedSoft.Antiforgery=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie + "; " + challengeCookie);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RequestToken);
        return client;
    }

    private static string ChallengeCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Last(cookie => cookie.StartsWith("MedSoft.AdministratorLogin=", StringComparison.Ordinal) &&
                       !cookie.StartsWith("MedSoft.AdministratorLogin=;", StringComparison.Ordinal));

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RequestToken);
    }

    private static void AssertNoRefreshCookie(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Should().NotContain(cookie => cookie.StartsWith("MedSoft.Refresh=", StringComparison.Ordinal));
    }
}
