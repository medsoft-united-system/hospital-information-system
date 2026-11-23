using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class BrowserTabSessionHandlerTests
{
    private const string Header = "X-MedSoft-Tab-Id";
    private const string TabId = "11223344556677889900aabbccddeeff";
    private static readonly Uri ApiOrigin = new("https://localhost:5201/");

    [Fact]
    public async Task AdministratorLoginAndLogout_ShouldSelectSameTabForChallengeCsrfAndTokens()
    {
        var state = new SessionState();
        var csrf = new AntiforgeryTokenStore();
        var operations = new AuthenticationOperationCoordinator();
        var paths = new List<string>();
        var administrator = new AuthenticationTokensDto("administrator-access", DateTimeOffset.UtcNow.AddMinutes(5),
            new AuthenticationSessionDto(Guid.NewGuid(), "admin", ApplicationRoles.Administrator, null, null));
        using var http = CreateClient(state, csrf, operations, new TabSession(_ => Task.FromResult(TabId)), request =>
        {
            request.Headers.GetValues(Header).Should().Equal(TabId);
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/api/auth/csrf" => Json(new AntiforgeryTokenDto("csrf")),
                "/api/auth/admin/login" or "/api/auth/admin/challenge" =>
                    Json(new AdministratorLoginChallengeDto("admin", DateTimeOffset.UtcNow.AddMinutes(5))),
                "/api/auth/admin/confirm" => Json(administrator),
                "/api/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException("Unexpected request.")
            };
        });
        http.DefaultRequestHeaders.Add(Header, "untrusted-default-tab");
        var api = new SessionApiClient(http, state, csrf, operations);

        await api.BeginAdministratorLoginAsync(new BeginAdministratorLoginRequest("admin", "Synthetic-Password-123"),
            CancellationToken.None);
        using var challenge = await http.GetAsync("api/auth/admin/challenge");
        await api.ConfirmAdministratorLoginAsync(new ConfirmAdministratorLoginRequest("123456"), CancellationToken.None);
        state.Current!.Id.Should().Be(administrator.Session.Id);
        await api.LogoutAsync(CancellationToken.None);

        paths.Should().Equal("/api/auth/csrf", "/api/auth/admin/login", "/api/auth/admin/challenge",
            "/api/auth/csrf", "/api/auth/admin/confirm", "/api/auth/csrf", "/api/auth/logout");
        state.Current.Should().BeNull();
        state.AccessToken.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAndRetry_ShouldPreserveTabAndReplaceCallerSuppliedSelectors()
    {
        var state = new SessionState();
        var original = MedicalTokens("expired-access");
        state.SetTokens(original);
        var paths = new List<string>();
        using var http = CreateClient(state, new AntiforgeryTokenStore { RequestToken = "old-csrf" },
            new AuthenticationOperationCoordinator(), new TabSession(_ => Task.FromResult(TabId)), request =>
            {
                request.Headers.GetValues(Header).Should().Equal(TabId);
                paths.Add(request.RequestUri!.AbsolutePath);
                return request.RequestUri.AbsolutePath switch
                {
                    "/api/auth/csrf" => Json(new AntiforgeryTokenDto("fresh-csrf")),
                    "/api/auth/refresh" => Json(original with { AccessToken = "fresh-access" }),
                    "/api/patients" => new HttpResponseMessage(request.Headers.Authorization?.Parameter == "fresh-access"
                        ? HttpStatusCode.OK : HttpStatusCode.Unauthorized),
                    _ => throw new InvalidOperationException("Unexpected request.")
                };
            });
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/patients")
        {
            Content = new StringContent("{}")
        };
        request.Headers.TryAddWithoutValidation(Header, ["another-tab", "duplicate-selector"]);

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        paths.Should().Equal("/api/patients", "/api/auth/csrf", "/api/auth/refresh", "/api/auth/csrf", "/api/patients");
        state.AccessToken.Should().Be("fresh-access");
    }

    [Fact]
    public async Task Send_SessionChangedWhileResolvingTab_ShouldNotSubmitPreviousRequest()
    {
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tab = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new SessionState();
        state.SetTokens(MedicalTokens("previous-access"));
        var requests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), new AuthenticationOperationCoordinator(),
            new TabSession(async cancellationToken =>
            {
                resolving.TrySetResult();
                return await tab.Task.WaitAsync(cancellationToken);
            }), _ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var pending = http.PostAsync("api/patients", new StringContent("{}"));
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = MedicalTokens("another-user-access");
        state.SetTokens(next);
        tab.SetResult(TabId);

        var action = async () => { using var response = await pending; };

        await action.Should().ThrowAsync<ApiException>().WithMessage("Сессия изменилась*");
        requests.Should().Be(0);
        state.AccessToken.Should().Be("another-user-access");
        state.Current!.Id.Should().Be(next.Session.Id);
    }

    [Fact]
    public async Task Send_CancelledDuringTabResolution_ShouldNotSendOrClearCurrentSession()
    {
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new SessionState();
        state.SetTokens(MedicalTokens("current-access"));
        var requests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), new AuthenticationOperationCoordinator(),
            new TabSession(async cancellationToken =>
            {
                resolving.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return TabId;
            }), _ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var cancellation = new CancellationTokenSource();
        var pending = http.GetAsync("api/patients", cancellation.Token);
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var action = async () => { using var response = await pending; };

        await action.Should().ThrowAsync<OperationCanceledException>();
        requests.Should().Be(0);
        state.AccessToken.Should().Be("current-access");
    }

    [Theory]
    [InlineData("")]
    [InlineData("another-tab")]
    [InlineData("11223344556677889900AABBCCDDEEFF")]
    [InlineData("11223344556677889900aabbccddeeff,11223344556677889900aabbccddeeff")]
    public async Task Send_InvalidTabIdentifier_ShouldFailBeforeSendingCredentials(string tabId)
    {
        var state = new SessionState();
        state.SetTokens(MedicalTokens("private-access"));
        var requests = 0;
        using var http = CreateClient(state, new AntiforgeryTokenStore(), new AuthenticationOperationCoordinator(),
            new TabSession(_ => Task.FromResult(tabId)), _ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var action = async () => { using var response = await http.GetAsync("api/patients"); };

        await action.Should().ThrowAsync<InvalidOperationException>();
        requests.Should().Be(0);
        state.AccessToken.Should().Be("private-access");
    }

    [Fact]
    public async Task Send_AnotherOrigin_ShouldRejectBeforeResolvingTabIdentifier()
    {
        var lookups = 0;
        var requests = 0;
        using var http = CreateClient(new SessionState(), new AntiforgeryTokenStore(), new AuthenticationOperationCoordinator(),
            new TabSession(_ =>
            {
                lookups++;
                return Task.FromResult(TabId);
            }), _ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var action = async () => { using var response = await http.GetAsync("https://attacker.invalid/api/patients"); };

        await action.Should().ThrowAsync<InvalidOperationException>();
        lookups.Should().Be(0);
        requests.Should().Be(0);
    }

    private static AuthenticationTokensDto MedicalTokens(string accessToken) =>
        new(accessToken, DateTimeOffset.UtcNow.AddMinutes(5), new AuthenticationSessionDto(Guid.NewGuid(), "doctor",
            ApplicationRoles.MedicalWorker, Guid.NewGuid(), MedicalWorkerRole.Doctor));

    private static HttpClient CreateClient(SessionState state, AntiforgeryTokenStore csrf,
        AuthenticationOperationCoordinator operations, IBrowserTabSession tab,
        Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new BrowserSessionHandler(state, csrf, operations, ApiOrigin, tab)
        {
            InnerHandler = new ResponseHandler(send)
        }) { BaseAddress = ApiOrigin };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class TabSession(Func<CancellationToken, Task<string>> resolve) : IBrowserTabSession
    {
        public Task<string> GetIdAsync(CancellationToken cancellationToken) => resolve(cancellationToken);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
