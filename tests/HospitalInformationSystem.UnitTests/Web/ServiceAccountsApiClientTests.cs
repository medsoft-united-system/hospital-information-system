using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Models.ServiceAccounts;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class ServiceAccountsApiClientTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("{\"items\":[]}")]
    [InlineData("<html>bad</html>")]
    public async Task List_RejectsMalformedSuccess(string body)
    {
        using var http = Client((_, _) => Task.FromResult(Raw(body)));
        var request = () => new ServiceAccountsApiClient(http).ListAsync(CancellationToken.None);
        await request.Should().ThrowAsync<ApiException>().WithMessage("*некорректный ответ*");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("role")]
    [InlineData("null-role")]
    [InlineData("empty-role")]
    [InlineData("wrong-case-role")]
    [InlineData("prefix")]
    [InlineData("date")]
    [InlineData("duplicate")]
    public async Task List_RejectsSemanticallyInvalidAccounts(string invalid)
    {
        var account = Account();
        var broken = invalid switch
        {
            "id" => account with { Id = Guid.Empty }, "version" => account with { Version = 0 },
            "name" => account with { Name = "ab" }, "role" => account with { Role = "Administrator" },
            "null-role" => account with { Role = null! }, "empty-role" => account with { Role = "" },
            "wrong-case-role" => account with { Role = "Hl7Import" },
            "prefix" => account with { KeyPrefix = "msa_wrong." },
            "date" => account with { UpdatedAtUtc = account.CreatedAtUtc.AddSeconds(-1) }, _ => account
        };
        using var http = Client((_, _) => Task.FromResult(Json(invalid == "duplicate" ? new[] { broken, broken } : [broken])));
        var request = () => new ServiceAccountsApiClient(http).ListAsync(CancellationToken.None);
        await request.Should().ThrowAsync<ApiException>();
    }

    [Theory]
    [InlineData(ServiceAccountRoles.Lis)]
    [InlineData(ServiceAccountRoles.Hl7Import)]
    public async Task Create_SerializesRoleAndAcceptsSelectedRole(string role)
    {
        var account = Account() with { Role = role };
        using var http = Client(async (request, token) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            body.RootElement.GetProperty("role").GetString().Should().Be(role);
            return Raw(JsonSerializer.Serialize(new ServiceAccountKeyResultDto(account, Key(account), 0),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), HttpStatusCode.Created);
        });

        var saved = await new ServiceAccountsApiClient(http).CreateAsync(new CreateServiceAccountRequest(account.Name, role), CancellationToken.None);

        saved.Account.Role.Should().Be(role);
    }

    [Fact]
    public async Task Create_RejectsResponseWithAnotherSupportedRole()
    {
        var account = Account();
        using var http = Client((_, _) => Task.FromResult(Raw(JsonSerializer.Serialize(
            new ServiceAccountKeyResultDto(account, Key(account), 0), new JsonSerializerOptions(JsonSerializerDefaults.Web)), HttpStatusCode.Created)));

        var create = () => new ServiceAccountsApiClient(http).CreateAsync(
            new CreateServiceAccountRequest(account.Name, ServiceAccountRoles.Hl7Import), CancellationToken.None);

        await create.Should().ThrowAsync<ApiException>();
    }

    [Fact]
    public async Task Create_Requires201AndDoesNotEchoRemoteDiagnostics()
    {
        var account = Account();
        var secret = Key(account);
        using var incorrectStatus = Client((_, _) => Task.FromResult(Json(new ServiceAccountKeyResultDto(account, secret, 0))));
        var create = () => new ServiceAccountsApiClient(incorrectStatus).CreateAsync(new CreateServiceAccountRequest(account.Name), CancellationToken.None);
        (await create.Should().ThrowAsync<ApiException>()).Which.ToString().Should().NotContain(secret);
        using var failure = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = JsonContent.Create(new { detail = secret, title = secret }) }));
        var rejected = () => new ServiceAccountsApiClient(failure).CreateAsync(new CreateServiceAccountRequest(account.Name), CancellationToken.None);
        (await rejected.Should().ThrowAsync<ApiException>()).Which.ToString().Should().NotContain(secret);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("account")]
    [InlineData("count")]
    [InlineData("duplicate-json")]
    [InlineData("wrong-casing")]
    public async Task Create_RejectsInvalidKeyRepresentation(string invalid)
    {
        var account = Account();
        var result = new ServiceAccountKeyResultDto(account, Key(account), 0);
        if (invalid == "key") result = result with { ApiKey = account.KeyPrefix + new string('!', 43) };
        if (invalid == "account") result = result with { ApiKey = "msa_" + Guid.NewGuid().ToString("N") + "." + new string('A', 43) };
        if (invalid == "count") result = result with { RevokedSessionCount = 1 };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (invalid == "duplicate-json") json = json.Replace("\"apiKey\":", "\"apiKey\":\"secret-reflection\",\"apiKey\":");
        if (invalid == "wrong-casing") json = json.Replace("\"apiKey\":", "\"ApiKey\":");
        using var http = Client((_, _) => Task.FromResult(Raw(json, HttpStatusCode.Created)));
        var request = () => new ServiceAccountsApiClient(http).CreateAsync(new CreateServiceAccountRequest(account.Name), CancellationToken.None);
        (await request.Should().ThrowAsync<ApiException>()).Which.ToString().Should().NotContain(result.ApiKey).And.NotContain("secret-reflection");
    }

    [Fact]
    public async Task Mutations_SendExpectedVersionAndRejectAnotherAccountResponse()
    {
        var expected = Account();
        uint version = 0;
        using var http = Client(async (request, token) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be($"/api/service-accounts/{expected.Id:D}/rotate-key");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            version = body.RootElement.GetProperty("version").GetUInt32();
            var other = Account();
            return Json(new ServiceAccountKeyResultDto(other, Key(other), 0));
        });
        var request = () => new ServiceAccountsApiClient(http).RotateKeyAsync(expected.Id,
            new RotateServiceAccountKeyRequest(expected.Version), CancellationToken.None);
        await request.Should().ThrowAsync<ApiException>();
        version.Should().Be(expected.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyIssuingPost_IsNotReplayedAfter401(bool rotate)
    {
        var posts = 0;
        var refreshes = 0;
        var state = new SessionState();
        state.SetTokens(new AuthenticationTokensDto("old-access", DateTimeOffset.UtcNow.AddMinutes(1),
            new AuthenticationSessionDto(Guid.NewGuid(), "admin", ApplicationRoles.Administrator, null, null)));
        using var handler = new BrowserSessionHandler(state, new AntiforgeryTokenStore { RequestToken = "csrf" },
            new AuthenticationOperationCoordinator(), new Uri("https://localhost/"))
        {
            InnerHandler = new ResponseHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/auth/refresh") refreshes++;
                else if (request.Method == HttpMethod.Post) posts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            })
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var api = new ServiceAccountsApiClient(http);
        Func<Task> request = rotate
            ? () => api.RotateKeyAsync(Guid.NewGuid(), new RotateServiceAccountKeyRequest(1), CancellationToken.None)
            : () => api.CreateAsync(new CreateServiceAccountRequest("lab"), CancellationToken.None);
        await request.Should().ThrowAsync<ApiException>();
        posts.Should().Be(1);
        refreshes.Should().Be(0);
    }

    internal static ServiceAccountDto Account() => NewAccount(Guid.NewGuid());
    private static ServiceAccountDto NewAccount(Guid id) => new(id, "LIS лаборатории", "LIS", true,
        new DateTimeOffset(2026, 11, 10, 9, 10, 11, TimeSpan.Zero), new DateTimeOffset(2026, 11, 10, 9, 10, 11, TimeSpan.Zero), 15, $"msa_{id:N}.");
    internal static string Key(ServiceAccountDto account) => account.KeyPrefix +
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpResponseMessage Raw(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ResponseHandler(send)) { BaseAddress = new Uri("https://localhost/") };
    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
