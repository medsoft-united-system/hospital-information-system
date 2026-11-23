using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class BrowserTokenRefreshTests
{
    private static readonly Uri ApiOrigin = new("https://localhost:5201/");

    [Fact]
    public async Task Send_ConcurrentUnauthorizedResponses_ShouldRefreshOnceAndRetryWithNewAccessToken()
    {
        var state = new SessionState();
        var previous = AuthenticationTokens("expired-access");
        state.SetTokens(previous);
        var refreshed = previous with { AccessToken = "fresh-access" };
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        var requests = new ConcurrentQueue<string?>();
        using var http = CreateClient(state, new AntiforgeryTokenStore(), async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
                return Json(new AntiforgeryTokenDto("refresh-csrf"));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
            {
                Interlocked.Increment(ref refreshes);
                request.Method.Should().Be(HttpMethod.Post);
                request.Headers.GetValues("X-CSRF-TOKEN").Should().Equal("refresh-csrf");
                refreshEntered.TrySetResult();
                return await refreshResponse.Task.WaitAsync(token);
            }
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            var access = request.Headers.Authorization.Parameter;
            requests.Enqueue(access);
            return new HttpResponseMessage(access == "fresh-access" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        });

        var first = http.GetAsync("api/patients");
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = http.GetAsync("api/diagnoses");
        refreshResponse.SetResult(Json(refreshed));
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(5));

        refreshes.Should().Be(1);
        requests.Count(access => access == "expired-access").Should().Be(2);
        requests.Count(access => access == "fresh-access").Should().Be(2);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        state.AccessToken.Should().Be("fresh-access");
    }

    [Fact]
    public async Task Send_UnauthorizedPostAfterRefresh_ShouldRetryBodyOnlyOnceAndClearRejectedSession()
    {
        var state = new SessionState();
        var previous = AuthenticationTokens("old-access");
        state.SetTokens(previous);
        var tokens = new AntiforgeryTokenStore();
        var refreshes = 0;
        var csrfRequests = 0;
        var attempts = new List<(string Body, string? Access, string Csrf)>();
        using var http = CreateClient(state, tokens, async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
                return Json(new AntiforgeryTokenDto("csrf-" + ++csrfRequests));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
            {
                refreshes++;
                return Json(previous with { AccessToken = "new-access" });
            }
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
            attempts.Add((await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.Parameter, request.Headers.GetValues("X-CSRF-TOKEN").Single()));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        const string body = "{\"firstName\":\"Иван\",\"lastName\":\"Иванов\"}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await http.PostAsync("api/patients", content);

        refreshes.Should().Be(1);
        attempts.Should().Equal((body, "old-access", "csrf-1"), (body, "new-access", "csrf-3"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        state.Current.Should().BeNull();
        state.AccessToken.Should().BeNull();
        tokens.RequestToken.Should().BeNull();
    }

    [Fact]
    public async Task Send_PostTimeout_ShouldPropagateWithoutRetryingMutationOrRefreshing()
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("current-access"));
        var mutations = 0;
        var refreshes = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
                return Task.FromResult(Json(new AntiforgeryTokenDto("csrf")));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh") refreshes++;
            else mutations++;
            throw new TaskCanceledException("Synthetic transport timeout after sending a mutation.");
        });

        var action = () => http.PostAsync("api/patients", new StringContent("{}"));

        await action.Should().ThrowAsync<TaskCanceledException>();
        mutations.Should().Be(1);
        refreshes.Should().Be(0);
        state.AccessToken.Should().Be("current-access");
    }

    [Theory]
    [InlineData("https://attacker.invalid/api/patients")]
    [InlineData("http://localhost:5201/api/patients")]
    [InlineData("https://localhost:5202/api/patients")]
    public async Task Send_AnotherOrigin_ShouldRejectBeforeSendingCookiesOrBearer(string destination)
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("private-access"));
        var requests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), (_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var action = () => http.PostAsync(destination, new StringContent("{}"));

        await action.Should().ThrowAsync<InvalidOperationException>();
        requests.Should().Be(0);
        state.AccessToken.Should().Be("private-access");
    }

    [Fact]
    public async Task Send_LateUnauthorizedResponseFromOldIdentity_ShouldPreserveNewLoginWithoutRetry()
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("old-user-access"));
        var originalResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), (_, _) =>
        {
            requests++;
            return originalResponse.Task;
        });
        var pending = http.GetAsync("api/patients");
        var newLogin = AuthenticationTokens("new-user-access");
        state.SetTokens(newLogin);

        originalResponse.SetResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var response = await pending;

        requests.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        state.Current!.Id.Should().Be(newLogin.Session.Id);
        state.AccessToken.Should().Be("new-user-access");
    }

    [Fact]
    public async Task Refresh_LateResponseFromOldIdentity_ShouldPreserveNewLogin()
    {
        var state = new SessionState();
        var old = AuthenticationTokens("old-access");
        state.SetTokens(old);
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalRequests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
                return Json(new AntiforgeryTokenDto("csrf"));
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
            {
                refreshEntered.SetResult();
                return await refreshResponse.Task.WaitAsync(cancellationToken);
            }
            normalRequests++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var pending = http.GetAsync("api/patients");
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newLogin = AuthenticationTokens("another-user-access");
        state.SetTokens(newLogin);

        refreshResponse.SetResult(Json(old with { AccessToken = "old-refreshed-access" }));
        using var response = await pending;

        normalRequests.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        state.Current!.Id.Should().Be(newLogin.Session.Id);
        state.AccessToken.Should().Be("another-user-access");
    }

    [Theory]
    [InlineData("api/auth/login")]
    [InlineData("api/auth/logout")]
    [InlineData("api/auth/refresh")]
    [InlineData("api/auth/setup/begin")]
    public async Task Send_AuthenticationEndpointUnauthorized_ShouldReturnWithoutRecursiveRefresh(string endpoint)
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("current-access"));
        var endpoints = new List<string>();
        using var http = CreateClient(state, new AntiforgeryTokenStore(), (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
                return Task.FromResult(Json(new AntiforgeryTokenDto("csrf")));
            endpoints.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        using var response = await http.PostAsync(endpoint, null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        endpoints.Should().Equal("/" + endpoint);
        state.AccessToken.Should().Be("current-access");
    }

    private static AuthenticationTokensDto AuthenticationTokens(string accessToken) =>
        new(accessToken, DateTimeOffset.UtcNow.AddMinutes(5), new AuthenticationSessionDto(Guid.NewGuid(),
            "doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(), MedicalWorkerRole.Doctor));

    private static HttpClient CreateClient(SessionState state, AntiforgeryTokenStore tokens,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new BrowserSessionHandler(state, tokens, new AuthenticationOperationCoordinator(), ApiOrigin)
        { InnerHandler = new ResponseHandler(send) })
        {
            BaseAddress = ApiOrigin,
            Timeout = TimeSpan.FromSeconds(10)
        };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value)
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
