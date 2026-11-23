using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Identity;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class AdministratorSetupApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    private const string OwnerToken = "owner-of-the-fresh-setup-integration-instance-256-bit";
    private const string Password = "Initial.Admin-Password.138!";

    [Fact]
    public async Task Setup_FreshHostAndRestart_ShouldRequireOwnerAndTwoFactorsWithoutReopeningSetup()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        SetupChallengeDto challenge;
        using (var host = NewHost(postgres))
        {
            using var client = NewClient(host);
            (await client.GetFromJsonAsync<SetupStatusDto>("/api/auth/setup/status"))!.SetupRequired.Should().BeTrue();
            await RefreshCsrfAsync(client);
            (await client.PostAsJsonAsync("/api/auth/setup/begin", new BeginSetupRequest(Password, Password)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Add("X-Setup-Token", OwnerToken);
            using var begin = await client.PostAsJsonAsync("/api/auth/setup/begin", new BeginSetupRequest(Password, Password));
            begin.StatusCode.Should().Be(HttpStatusCode.OK);
            challenge = (await begin.Content.ReadFromJsonAsync<SetupChallengeDto>())!;
            (begin.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : []).Should()
                .NotContain(cookie => cookie.Contains("Refresh", StringComparison.Ordinal));
            (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/api/auth/setup/confirm", new ConfirmSetupRequest(challenge.ChallengeId, "invalid")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Users.AnyAsync(CancellationToken.None))
                    .Should().BeFalse();
            }
            var code = Code(host, challenge);
            (await client.PostAsJsonAsync("/api/auth/setup/confirm", new ConfirmSetupRequest(challenge.ChallengeId, code)))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.GetFromJsonAsync<SetupStatusDto>("/api/auth/setup/status"))!.SetupRequired.Should().BeFalse();
            (await client.PostAsJsonAsync("/api/auth/setup/begin", new BeginSetupRequest(Password, Password)))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        using var restarted = NewHost(postgres);
        using var restartedClient = NewClient(restarted);
        (await restartedClient.GetFromJsonAsync<SetupStatusDto>("/api/auth/setup/status"))!.SetupRequired.Should().BeFalse();
        await RefreshCsrfAsync(restartedClient);
        (await restartedClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(SecureHospitalApiFactory.AdminUserName, Password))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factory.AdvanceTime(TimeSpan.FromSeconds(31));
        using var login = await restartedClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(SecureHospitalApiFactory.AdminUserName, Password, Code(restarted, challenge)));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!;
        restartedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        (await restartedClient.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Confirm_ConcurrentHttpRequests_ShouldCreateAdministratorExactlyOnce()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        using var host = NewHost(postgres);
        using var client = NewClient(host);
        await RefreshCsrfAsync(client);
        client.DefaultRequestHeaders.Add("X-Setup-Token", OwnerToken);
        using var begin = await client.PostAsJsonAsync("/api/auth/setup/begin", new BeginSetupRequest(Password, Password));
        begin.EnsureSuccessStatusCode();
        var challenge = (await begin.Content.ReadFromJsonAsync<SetupChallengeDto>())!;
        var request = new ConfirmSetupRequest(challenge.ChallengeId, Code(host, challenge));

        var results = await Task.WhenAll(client.PostAsJsonAsync("/api/auth/setup/confirm", request),
            client.PostAsJsonAsync("/api/auth/setup/confirm", request));

        results.Select(result => result.StatusCode).Should().BeEquivalentTo(
            new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
        foreach (var result in results) result.Dispose();
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Users.CountAsync(CancellationToken.None))
            .Should().Be(1);
    }

    private WebApplicationFactory<Program> NewHost(PostgreSqlContainer postgres) => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString());
        builder.UseSetting("Authentication:Setup:TokenSha256", Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(OwnerToken))));
    });

    private static HttpClient NewClient(WebApplicationFactory<Program> host) => host.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

    private static string Code(WebApplicationFactory<Program> host, SetupChallengeDto challenge) =>
        new TotpCodeService().GetCode(challenge.SecretKey, host.Services.GetRequiredService<TimeProvider>().GetUtcNow());

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
