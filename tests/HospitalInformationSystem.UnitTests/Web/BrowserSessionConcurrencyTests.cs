using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class BrowserSessionConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_SessionChangedWhileBufferingBody_ShouldRejectBeforeSendingMutation(bool loggedOut)
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("original-access"));
        var requests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), (_, _) =>
        {
            requests++;
            return Task.FromResult(Json(new AntiforgeryTokenDto("csrf")));
        });
        using var body = new BlockingContent();
        var pending = http.PostAsync("api/patients", body);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (loggedOut) state.SetSession(null);
        else state.SetTokens(AuthenticationTokens("another-user-access"));
        body.Release.SetResult();

        var action = async () => { using var response = await pending; };

        await action.Should().ThrowAsync<ApiException>().WithMessage("*Сессия изменилась*");
        requests.Should().Be(0);
        state.AccessToken.Should().Be(loggedOut ? null : "another-user-access");
    }

    [Fact]
    public async Task Send_SessionChangedWhileLoadingAntiforgery_ShouldRejectBeforeSendingMutation()
    {
        var state = new SessionState();
        state.SetTokens(AuthenticationTokens("original-access"));
        var tokens = new AntiforgeryTokenStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = 0;
        using var http = CreateClient(state, tokens, async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Json(new AntiforgeryTokenDto("stale-csrf"));
            }
            mutations++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var pending = http.PostAsync("api/patients", new StringContent("{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.SetTokens(AuthenticationTokens("another-user-access"));
        tokens.Reset();
        release.SetResult();

        var action = async () => { using var response = await pending; };

        await action.Should().ThrowAsync<ApiException>().WithMessage("*Сессия изменилась*");
        mutations.Should().Be(0);
        tokens.RequestToken.Should().BeNull();
        state.AccessToken.Should().Be("another-user-access");
    }

    [Fact]
    public async Task Retry_SessionChangedWhileLoadingAntiforgery_ShouldRejectWithoutRepeatingMutation()
    {
        var state = new SessionState();
        var original = AuthenticationTokens("expired-access");
        state.SetTokens(original);
        var tokens = new AntiforgeryTokenStore { RequestToken = "original-csrf" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var csrfRequests = 0;
        var mutations = 0;
        using var http = CreateClient(state, tokens, async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/csrf")
            {
                if (++csrfRequests == 2)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
                return Json(new AntiforgeryTokenDto("csrf-" + csrfRequests));
            }
            if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
                return Json(original with { AccessToken = "refreshed-access" });
            mutations++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var pending = http.PostAsync("api/patients", new StringContent("{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.SetTokens(AuthenticationTokens("another-user-access"));
        tokens.Reset();
        release.SetResult();

        var action = async () => { using var response = await pending; };

        await action.Should().ThrowAsync<ApiException>().WithMessage("*Сессия изменилась*");
        mutations.Should().Be(1);
        tokens.RequestToken.Should().BeNull();
        state.AccessToken.Should().Be("another-user-access");
    }

    private static AuthenticationTokensDto AuthenticationTokens(string accessToken) =>
        new(accessToken, DateTimeOffset.UtcNow.AddMinutes(5), new AuthenticationSessionDto(Guid.NewGuid(),
            "doctor", ApplicationRoles.MedicalWorker, Guid.NewGuid(), MedicalWorkerRole.Doctor));

    private static HttpClient CreateClient(SessionState state, AntiforgeryTokenStore tokens,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new BrowserSessionHandler(state, tokens, new AuthenticationOperationCoordinator(), new Uri("https://localhost:5201/"))
        {
            InnerHandler = new ResponseHandler(send)
        }) { BaseAddress = new Uri("https://localhost:5201/") };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class BlockingContent : HttpContent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Entered.TrySetResult();
            await Release.Task;
            await stream.WriteAsync("{}"u8.ToArray());
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
