using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class BrowserAuthenticationOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_RefreshInProgress_ShouldApplyLoginOrLogoutAfterRefreshCookie(bool logout)
    {
        var state = new SessionState();
        var previous = AuthenticationTokens("expired-access");
        var next = AuthenticationTokens("another-user-access");
        state.SetTokens(previous);
        var operations = new AuthenticationOperationCoordinator();
        var tokens = new AntiforgeryTokenStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticationRequests = 0;
        string? cookie = "previous-refresh-cookie";
        using var http = CreateClient(state, tokens, operations, async (request, cancellationToken) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/csrf": return Json(new AntiforgeryTokenDto("csrf"));
                case "/api/auth/refresh":
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    cookie = "rotated-previous-cookie";
                    return Json(previous with { AccessToken = "refreshed-access" });
                case "/api/auth/login":
                    authenticationRequests++;
                    cookie = "another-user-cookie";
                    return Json(next);
                case "/api/auth/logout":
                    authenticationRequests++;
                    cookie = null;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(request.Headers.Authorization?.Parameter == "refreshed-access"
                        ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
            }
        });
        var api = new SessionApiClient(http, state, tokens, operations);
        var read = http.GetAsync("api/patients");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var authentication = logout ? api.LogoutAsync(CancellationToken.None)
            : api.LoginAsync(new LoginRequest("doctor", "Synthetic-Password-123"), CancellationToken.None);

        authenticationRequests.Should().Be(0);
        release.SetResult();
        try { using var response = await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ApiException exception) when (exception.Message.StartsWith("Сессия изменилась", StringComparison.Ordinal)) { }
        await authentication.WaitAsync(TimeSpan.FromSeconds(5));
        authenticationRequests.Should().Be(1);
        cookie.Should().Be(logout ? null : "another-user-cookie");
        state.AccessToken.Should().Be(logout ? null : "another-user-access");
    }

    [Fact]
    public async Task Refresh_LoginResponseBodyPending_ShouldWaitUntilLoginStateIsApplied()
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("previous-access"));
        var next = AuthenticationTokens("another-user-access");
        var operations = new AuthenticationOperationCoordinator();
        var tokens = new AntiforgeryTokenStore();
        var content = new DelayedJsonContent(next);
        var refreshes = 0;
        var cookie = "previous-cookie";
        using var http = CreateClient(state, tokens, operations, (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/csrf": return Task.FromResult(Json(new AntiforgeryTokenDto("csrf")));
                case "/api/auth/login":
                    cookie = "another-user-cookie";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                case "/api/auth/refresh":
                    refreshes++;
                    cookie = "unexpected-rotation";
                    return Task.FromResult(Json(next));
                default: return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
        });
        var api = new SessionApiClient(http, state, tokens, operations);
        var login = api.LoginAsync(new LoginRequest("doctor", "Synthetic-Password-123"), CancellationToken.None);
        await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = http.GetAsync("api/patients");

        refreshes.Should().Be(0);
        content.Release.SetResult();
        await login.WaitAsync(TimeSpan.FromSeconds(5));
        using var response = await read.WaitAsync(TimeSpan.FromSeconds(5));

        refreshes.Should().Be(0);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        cookie.Should().Be("another-user-cookie");
        state.Current!.Id.Should().Be(next.Session.Id);
    }

    [Fact]
    public async Task Login_CancelledWhileWaitingForRefresh_ShouldPreserveSessionAndReleaseCoordinator()
    {
        var state = new SessionState();
        var original = AuthenticationTokens("old-access");
        state.SetTokens(original);
        var operations = new AuthenticationOperationCoordinator();
        var tokens = new AntiforgeryTokenStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logins = 0;
        using var http = CreateClient(state, tokens, operations, async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf") return Json(new AntiforgeryTokenDto("csrf"));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Json(original with { AccessToken = "refreshed-access" });
            }
            if (request.RequestUri.AbsolutePath == "/api/auth/login")
            {
                logins++;
                return Json(AuthenticationTokens("next-login-access"));
            }
            return new HttpResponseMessage(request.Headers.Authorization?.Parameter == "refreshed-access"
                ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        });
        var api = new SessionApiClient(http, state, tokens, operations);
        var read = http.GetAsync("api/patients");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var login = api.LoginAsync(new LoginRequest("doctor", "Synthetic-Password-123"), cancellation.Token);
        cancellation.Cancel();
        var action = async () => await login;
        await action.Should().ThrowAsync<OperationCanceledException>();
        logins.Should().Be(0);
        release.SetResult();
        using var response = await read.WaitAsync(TimeSpan.FromSeconds(5));
        state.AccessToken.Should().Be("refreshed-access");
        await api.LoginAsync(new LoginRequest("doctor", "Synthetic-Password-123"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        logins.Should().Be(1);
        state.AccessToken.Should().Be("next-login-access");
    }

    [Fact]
    public async Task Refresh_CancelledWhileWaitingForLogin_ShouldPreserveSessionAndAntiforgery()
    {
        var state = new SessionState();
        var previous = AuthenticationTokens("previous-access");
        state.SetTokens(previous);
        var next = AuthenticationTokens("another-user-access");
        var operations = new AuthenticationOperationCoordinator();
        var tokens = new AntiforgeryTokenStore();
        var content = new DelayedJsonContent(next);
        var refreshes = 0;
        using var http = CreateClient(state, tokens, operations, (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/csrf": return Task.FromResult(Json(new AntiforgeryTokenDto("login-csrf")));
                case "/api/auth/login":
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                case "/api/auth/refresh":
                    refreshes++;
                    return Task.FromResult(Json(next));
                default: return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
        });
        var api = new SessionApiClient(http, state, tokens, operations);
        var login = api.LoginAsync(new LoginRequest("doctor", "Synthetic-Password-123"), CancellationToken.None);
        await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var read = http.GetAsync("api/patients", cancellation.Token);
        cancellation.Cancel();

        var action = async () => { using var response = await read; };
        await action.Should().ThrowAsync<OperationCanceledException>();

        refreshes.Should().Be(0);
        state.Current!.Id.Should().Be(previous.Session.Id);
        state.AccessToken.Should().Be("previous-access");
        tokens.RequestToken.Should().Be("login-csrf");
        content.Release.SetResult();
        await login.WaitAsync(TimeSpan.FromSeconds(5));
        state.Current!.Id.Should().Be(next.Session.Id);
        state.AccessToken.Should().Be("another-user-access");
    }

    private static AuthenticationTokensDto AuthenticationTokens(string accessToken) =>
        new(accessToken, DateTimeOffset.UtcNow.AddMinutes(5), new AuthenticationSessionDto(Guid.NewGuid(),
            "doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(), MedicalWorkerRole.Doctor));

    private static HttpClient CreateClient(SessionState state, AntiforgeryTokenStore tokens,
        AuthenticationOperationCoordinator operations,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new BrowserSessionHandler(state, tokens, operations, new Uri("https://localhost:5201/"))
        {
            InnerHandler = new ResponseHandler(send)
        }) { BaseAddress = new Uri("https://localhost:5201/") };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class DelayedJsonContent(AuthenticationTokensDto tokens) : HttpContent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Entered.TrySetResult();
            await Release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens)));
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
