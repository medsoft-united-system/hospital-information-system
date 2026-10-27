using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class BrowserRefreshIdentityTests
{
    [Theory]
    [InlineData("account")]
    [InlineData("account-role")]
    [InlineData("worker")]
    [InlineData("medical-role")]
    public async Task Refresh_IdentityChangedInAnotherTab_ShouldNotRepeatOriginalMutation(string changedField)
    {
        var state = new SessionState();
        var original = AuthenticationTokens("original-access");
        state.SetTokens(original);
        var refreshedSession = changedField switch
        {
            "account" => original.Session with { Id = Guid.NewGuid() },
            "account-role" => original.Session with { AccountRole = ApplicationRoles.Administrator },
            "worker" => original.Session with { MedicalWorkerId = Guid.NewGuid() },
            "medical-role" => original.Session with { MedicalWorkerRole = MedicalWorkerRole.Nurse },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };
        var tokens = new AntiforgeryTokenStore();
        var mutations = 0;
        var refreshes = 0;
        using var http = CreateClient(state, tokens, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf") return Json(new AntiforgeryTokenDto("csrf"));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
            {
                refreshes++;
                return Json(original with { AccessToken = "another-session-access", Session = refreshedSession });
            }
            mutations++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        using var response = await http.PostAsync("api/patients", new StringContent("{}"));

        refreshes.Should().Be(1);
        mutations.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        state.Current.Should().BeNull();
        state.AccessToken.Should().BeNull();
        tokens.RequestToken.Should().BeNull();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Send_AnonymousMutationUnauthorized_ShouldRequireExplicitSessionBeforeRetry(string method)
    {
        var state = new SessionState();
        var refreshes = 0;
        var mutations = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf") return Json(new AntiforgeryTokenDto("csrf"));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
            {
                refreshes++;
                return Json(AuthenticationTokens("unexpected-access"));
            }
            mutations++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), "api/patients") { Content = new StringContent("{}") };

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refreshes.Should().Be(0);
        mutations.Should().Be(1);
        state.Current.Should().BeNull();
    }

    [Fact]
    public async Task GetSession_ColdStart_ShouldRestoreSessionAndRetryRead()
    {
        var state = new SessionState();
        var restored = AuthenticationTokens("restored-access");
        var reads = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf") return Json(new AntiforgeryTokenDto("csrf"));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh") return Json(restored);
            reads++;
            return request.Headers.Authorization?.Parameter == "restored-access"
                ? Json(new SessionDto(restored.Session.Id, restored.Session.UserName, restored.Session.AccountRole,
                    restored.Session.MedicalWorkerId, restored.Session.MedicalWorkerRole))
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        using var response = await http.GetAsync("api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        reads.Should().Be(2);
        state.Current!.Id.Should().Be(restored.Session.Id);
        state.AccessToken.Should().Be("restored-access");
    }

    private static AuthenticationTokensDto AuthenticationTokens(string accessToken) =>
        new(accessToken, DateTimeOffset.UtcNow.AddMinutes(5), new AuthenticationSessionDto(Guid.NewGuid(),
            "doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(), MedicalWorkerRole.Doctor));

    private static HttpClient CreateClient(SessionState state, AntiforgeryTokenStore tokens,
        Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new BrowserSessionHandler(state, tokens, new AuthenticationOperationCoordinator(), new Uri("https://localhost:5201/"))
        {
            InnerHandler = new ResponseHandler(send)
        }) { BaseAddress = new Uri("https://localhost:5201/") };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
