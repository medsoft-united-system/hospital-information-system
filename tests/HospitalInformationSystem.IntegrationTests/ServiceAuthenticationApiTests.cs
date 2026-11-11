using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Contracts.ServiceAuth;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAuthenticationApiTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    [Fact]
    public async Task Token_ShouldIssueRealRs256PairWithoutCsrfCookiesOrSecretsInAudit()
    {
        using var admin = await factory.AdministratorAsync();
        var created = await ServiceAccountsApiTests.CreateAsync(admin);
        using var machine = factory.CreateMachineClient();

        using var response = await machine.PostAsJsonAsync("/api/service-auth/token", new ServiceTokenRequest(created.ApiKey));

        var tokens = await PairAsync(response);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                "accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        jwt.Header.Alg.Should().Be("RS256");
        jwt.Issuer.Should().Be(ServiceAuthenticationDefaults.Issuer);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be(ServiceAuthenticationDefaults.Audience);
        jwt.Claims.Single(claim => claim.Type == "sub").Value.Should().Be(created.Account.Id.ToString("D"));
        jwt.Claims.Single(claim => claim.Type == "account_kind").Value.Should().Be("service");
        jwt.Claims.Single(claim => claim.Type == "role").Value.Should().Be("LIS");
        jwt.Claims.Single(claim => claim.Type == "auth_ver").Value.Should().Be("1");
        var familyId = Guid.Parse(jwt.Claims.Single(claim => claim.Type == "sid").Value);
        var sessionId = Guid.Parse(jwt.Claims.Single(claim => claim.Type == "jti").Value);
        familyId.Should().NotBeEmpty();
        sessionId.Should().NotBeEmpty();
        tokens.RefreshToken.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        tokens.AccessTokenExpiresAt.Should().BeBefore(tokens.RefreshTokenExpiresAt);
        tokens.RefreshTokenExpiresAt.Offset.Should().Be(TimeSpan.Zero);

        using var authenticated = factory.CreateMachineClient(tokens.AccessToken);
        using var probe = await authenticated.GetAsync("/api/testConnection");
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertTechnicalResponse(probe);
        (await probe.Content.ReadFromJsonAsync<ServiceConnectionStatusDto>()).Should().BeEquivalentTo(
            new ServiceConnectionStatusDto("connected", created.Account.Id, "LIS", "5.0.0"));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var stored = await db.ServiceRefreshSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId);
        stored.ServiceAccountId.Should().Be(created.Account.Id);
        stored.FamilyId.Should().Be(familyId);
        stored.TokenHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Decode(tokens.RefreshToken))));
        stored.TokenHash.Should().NotBe(tokens.RefreshToken);
        stored.ConsumedAtUtc.Should().BeNull();
        stored.RevokedAtUtc.Should().BeNull();
        var audit = await db.AuditEvents.AsNoTracking().ToArrayAsync();
        audit.Should().Contain(row => row.EventType == "ServiceLogin" && row.ActorKind == "service" && row.ActorServiceAccountId == created.Account.Id
            && row.ActorUserId == null);
        audit.Should().NotContain(row => row.EventType == "UserLogin" && row.ActorServiceAccountId == created.Account.Id);
        foreach (var row in audit)
            (row.Message + row.MetadataJson).Should().NotContain(created.ApiKey)
                .And.NotContain(tokens.AccessToken).And.NotContain(tokens.RefreshToken);
    }

    [Theory]
    [InlineData("/api/service-auth/token", "{}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":null}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":\"\"}")]
    [InlineData("/api/service-auth/token", "{\"ApiKey\":\"value\"}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":\"one\",\"apiKey\":\"two\"}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":\"one\",\"userName\":\"admin\"}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":123}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":\"bad\\u0000key\"}")]
    [InlineData("/api/service-auth/token", "{\"apiKey\":\"bad\\ud800key\"}")]
    [InlineData("/api/service-auth/token", "[]")]
    [InlineData("/api/service-auth/token", "{")]
    [InlineData("/api/service-auth/refresh", "{}")]
    [InlineData("/api/service-auth/refresh", "{\"refreshToken\":null}")]
    [InlineData("/api/service-auth/refresh", "{\"refreshToken\":\"\"}")]
    [InlineData("/api/service-auth/refresh", "{\"RefreshToken\":\"value\"}")]
    [InlineData("/api/service-auth/refresh", "{\"refreshToken\":\"one\",\"refreshToken\":\"two\"}")]
    [InlineData("/api/service-auth/refresh", "{\"refreshToken\":\"one\",\"apiKey\":\"two\"}")]
    public async Task MalformedBody_ShouldUseTechnical400WithoutSettingHumanCookies(string route, string body)
    {
        using var machine = factory.CreateMachineClient();
        using var response = await machine.PostAsync(route, Json(body));
        await ErrorAsync(response, HttpStatusCode.BadRequest, ServiceAuthErrorCodes.InvalidRequest);
    }

    [Theory]
    [InlineData("/api/service-auth/token", "text/plain")]
    [InlineData("/api/service-auth/refresh", "application/fhir+json")]
    public async Task UnsupportedContentType_ShouldUseTechnical415(string route, string mediaType)
    {
        using var machine = factory.CreateMachineClient();
        using var response = await machine.PostAsync(route, new StringContent("{}", Encoding.UTF8, mediaType));
        await ErrorAsync(response, HttpStatusCode.UnsupportedMediaType, ServiceAuthErrorCodes.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("/api/service-auth/token", "apiKey")]
    [InlineData("/api/service-auth/refresh", "refreshToken")]
    public async Task OversizedBody_ShouldUseTechnical413(string route, string property)
    {
        using var machine = factory.CreateMachineClient();
        using var response = await machine.PostAsync(route, Json(JsonSerializer.Serialize(
            new Dictionary<string, string> { [property] = new('x', 64 * 1024 + 1) })));
        await ErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ServiceAuthErrorCodes.RequestTooLarge);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CredentialBody_AtPublished64KiBLimit_ShouldAcceptValidJson(bool refresh, bool unknownLength)
    {
        var account = await factory.CreateAccountAsync();
        var credential = refresh ? (await factory.IssueAsync(account.ApiKey)).RefreshToken : account.ApiKey;
        var property = refresh ? "refreshToken" : "apiKey";
        var route = refresh ? "/api/service-auth/refresh" : "/api/service-auth/token";
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [property] = credential });
        var bytes = Encoding.UTF8.GetBytes(json.PadRight(64 * 1024));
        bytes.Length.Should().Be(64 * 1024);
        using HttpContent content = unknownLength ? new UnknownLengthJsonContent(bytes) : Json(Encoding.UTF8.GetString(bytes));
        using var machine = factory.CreateMachineClient();

        using var response = await machine.PostAsync(route, content);

        await PairAsync(response);
    }

    [Theory]
    [InlineData("/api/service-auth/token", "apiKey")]
    [InlineData("/api/service-auth/refresh", "refreshToken")]
    public async Task OversizedBodyWithoutContentLength_ShouldStillUseTechnical413(string route, string property)
    {
        using var machine = factory.CreateMachineClient();
        using var body = new UnknownLengthJsonContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new Dictionary<string, string> { [property] = new('x', 64 * 1024 + 1) })));
        using var response = await machine.PostAsync(route, body);
        await ErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ServiceAuthErrorCodes.RequestTooLarge);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public async Task UnknownOrNoncanonicalRefresh_ShouldReturnTechnical401WithoutDecoderErrors(string refreshToken)
    {
        using var machine = factory.CreateMachineClient();
        using var response = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(refreshToken));
        await ErrorAsync(response, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
    }

    [Fact]
    public async Task WrongKeyForExistingId_ShouldRejectWithoutAttributingUnverifiedAccountToAudit()
    {
        var account = await factory.CreateAccountAsync();
        var wrongKey = account.ApiKey[..37] + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Guid[] previousFailures;
        await using (var before = factory.Services.CreateAsyncScope())
            previousFailures = await before.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents
                .Where(row => row.EventType == "LoginFailed").Select(row => row.EventId).ToArrayAsync();
        using var machine = factory.CreateMachineClient();
        using var response = await machine.PostAsJsonAsync("/api/service-auth/token", new ServiceTokenRequest(wrongKey));
        await ErrorAsync(response, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidCredentials);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.ServiceRefreshSessions.AnyAsync(row => row.ServiceAccountId == account.Account.Id)).Should().BeFalse();
        var failures = await db.AuditEvents.AsNoTracking().Where(row => row.EventType == "LoginFailed" &&
            !previousFailures.Contains(row.EventId)).ToArrayAsync();
        failures.Should().NotBeEmpty();
        foreach (var row in failures)
        {
            row.ActorServiceAccountId.Should().BeNull();
            row.ActorUserId.Should().BeNull();
            (row.Message + row.MetadataJson).Should().NotContain(wrongKey)
                .And.NotContain(account.Account.Id.ToString("D"));
        }
    }

    [Fact]
    public async Task OrdinaryRefresh_ShouldKeepPreviousAccessValidAndMaintainFamilyExpiry()
    {
        var account = await factory.CreateAccountAsync();
        var first = await factory.IssueAsync(account.ApiKey);
        factory.AdvanceTime(TimeSpan.FromSeconds(20));
        using var machine = factory.CreateMachineClient();
        using var response = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(first.RefreshToken));
        var next = await PairAsync(response);
        next.RefreshToken.Should().NotBe(first.RefreshToken);
        next.AccessToken.Should().NotBe(first.AccessToken);
        next.RefreshTokenExpiresAt.Should().Be(first.RefreshTokenExpiresAt);
        var handler = new JwtSecurityTokenHandler();
        handler.ReadJwtToken(next.AccessToken).Claims.Single(claim => claim.Type == "sid").Value.Should()
            .Be(handler.ReadJwtToken(first.AccessToken).Claims.Single(claim => claim.Type == "sid").Value);
        foreach (var token in new[] { first.AccessToken, next.AccessToken })
        {
            using var authenticated = factory.CreateMachineClient(token);
            (await authenticated.GetAsync("/api/testConnection")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var sessions = await db.ServiceRefreshSessions.AsNoTracking()
            .Where(row => row.ServiceAccountId == account.Account.Id).ToArrayAsync();
        sessions.Should().HaveCount(2).And.OnlyContain(row => row.RevokedAtUtc == null);
        sessions.Should().ContainSingle(row => row.ConsumedAtUtc != null && row.ReplacedBySessionId != null);
    }

    [Fact]
    public async Task ReusedRefresh_ShouldRevokeItsFamilyButPreserveIndependentLogin()
    {
        var account = await factory.CreateAccountAsync();
        var initial = await factory.IssueAsync(account.ApiKey);
        var independent = await factory.IssueAsync(account.ApiKey);
        using var machine = factory.CreateMachineClient();
        using var refreshed = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(initial.RefreshToken));
        var continuation = await PairAsync(refreshed);

        using var replay = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(initial.RefreshToken));
        await ErrorAsync(replay, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
        foreach (var token in new[] { initial.AccessToken, continuation.AccessToken })
        {
            using var authenticated = factory.CreateMachineClient(token);
            using var refused = await authenticated.GetAsync("/api/testConnection");
            await ErrorAsync(refused, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.Unauthorized);
        }
        using var replacement = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(continuation.RefreshToken));
        await ErrorAsync(replacement, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
        using var other = factory.CreateMachineClient(independent.AccessToken);
        (await other.GetAsync("/api/testConnection")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyRotationOrDeactivation_ShouldInvalidateEveryOldAccessAndRefresh(bool deactivate)
    {
        var account = await factory.CreateAccountAsync();
        var first = await factory.IssueAsync(account.ApiKey);
        var second = await factory.IssueAsync(account.ApiKey);
        string? newKey = null;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            if (deactivate)
                (await accounts.SetActiveAsync(account.Account.Id,
                    new SetServiceAccountActiveRequest(false, account.Account.Version), CancellationToken.None))
                    .RevokedSessionCount.Should().Be(2);
            else
                newKey = (await accounts.RotateKeyAsync(account.Account.Id,
                    new RotateServiceAccountKeyRequest(account.Account.Version), CancellationToken.None)).ApiKey;
        }
        using var machine = factory.CreateMachineClient();
        foreach (var pair in new[] { first, second })
        {
            using var authenticated = factory.CreateMachineClient(pair.AccessToken);
            using var refused = await authenticated.GetAsync("/api/testConnection");
            await ErrorAsync(refused, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.Unauthorized);
            using var refresh = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(pair.RefreshToken));
            await ErrorAsync(refresh, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidRefresh);
        }
        using var oldKey = await machine.PostAsJsonAsync("/api/service-auth/token", new ServiceTokenRequest(account.ApiKey));
        await ErrorAsync(oldKey, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.InvalidCredentials);
        if (newKey is not null)
        {
            var fresh = await factory.IssueAsync(newKey);
            using var authenticated = factory.CreateMachineClient(fresh.AccessToken);
            (await authenticated.GetAsync("/api/testConnection")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ExpiredAccess_ShouldFailAtExactExpiryWhileRefreshCanIssueNewAccess()
    {
        var account = await factory.CreateAccountAsync();
        var pair = await factory.IssueAsync(account.ApiKey);
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        factory.AdvanceTime(pair.AccessTokenExpiresAt - now);
        using var expired = factory.CreateMachineClient(pair.AccessToken);
        using var refused = await expired.GetAsync("/api/testConnection");
        await ErrorAsync(refused, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.Unauthorized);
        using var machine = factory.CreateMachineClient();
        using var refreshed = await machine.PostAsJsonAsync("/api/service-auth/refresh", new ServiceRefreshRequest(pair.RefreshToken));
        var next = await PairAsync(refreshed);
        using var authenticated = factory.CreateMachineClient(next.AccessToken);
        (await authenticated.GetAsync("/api/testConnection")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    internal static async Task<ServiceTokenPairDto> PairAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        AssertTechnicalResponse(response);
        return (await response.Content.ReadFromJsonAsync<ServiceTokenPairDto>())!;
    }

    internal static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        AssertTechnicalResponse(response);
        using var json = JsonDocument.Parse(text);
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("code", "message", "traceId");
        var error = (await response.Content.ReadFromJsonAsync<ServiceAuthErrorDto>())!;
        error.Code.Should().Be(code);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.TraceId.Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain("Npgsql").And.NotContain("StackTrace").And.NotContain("Exception");
    }

    internal static void AssertTechnicalResponse(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        response.Headers.Location.Should().BeNull();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static byte[] Decode(string token) => Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _body;

        public UnknownLengthJsonContent(byte[] body)
        {
            _body = body;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_body.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
