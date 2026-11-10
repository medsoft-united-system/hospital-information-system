using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Web;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Forms;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class ServiceAccountsPageTests
{
    [Fact]
    public async Task CreatedKey_IsShownOnceAndAccountSurvivesRefreshFailure()
    {
        var account = ServiceAccountsApiClientTests.Account();
        var key = ServiceAccountsApiClientTests.Key(account);
        using var page = Page(new StubGateway
        {
            Create = (input, _) => { input.Name.Should().Be(account.Name); return Task.FromResult(new ServiceAccountKeyResultDto(account, key, 0)); },
            List = _ => throw new HttpRequestException()
        });
        Invoke(page, "BeginCreate");
        Get<ServiceAccountForm>(page, "_form").Name = " " + account.Name + " ";

        await InvokeAsync(page, "SaveAsync");

        Get<IReadOnlyList<ServiceAccountDto>>(page, "_accounts").Should().Equal(account);
        Get<string>(page, "_apiKey").Should().Be(key);
        Get<bool>(page, "_formOpen").Should().BeFalse();
        Get<string>(page, "_listError").Should().NotBeNullOrWhiteSpace();
        Invoke(page, "BeginCreate");
        Get<bool>(page, "_formOpen").Should().BeFalse();
        Invoke(page, "CloseKey");
        Get<string?>(page, "_apiKey").Should().BeNull();
        Get<string?>(page, "_secretAccountName").Should().BeNull();
        Invoke(page, "BeginCreate");
        Get<bool>(page, "_formOpen").Should().BeTrue();
        Get<string?>(page, "_apiKey").Should().BeNull();
    }

    [Fact]
    public async Task DoubleSubmissionAndNavigation_CannotIssueAnotherKeyOrKeepLateSecret()
    {
        var account = ServiceAccountsApiClientTests.Account();
        var completion = new TaskCompletionSource<ServiceAccountKeyResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var page = Page(new StubGateway { Create = (_, _) => { calls++; return completion.Task; } });
        Invoke(page, "BeginCreate");
        Get<ServiceAccountForm>(page, "_form").Name = account.Name;
        var request = InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "SaveAsync");
        Invoke(page, "BeginEdit", account);
        calls.Should().Be(1);
        page.Dispose();
        completion.SetResult(new ServiceAccountKeyResultDto(account, ServiceAccountsApiClientTests.Key(account), 0));
        await request;
        Get<string?>(page, "_apiKey").Should().BeNull();
        Get<IReadOnlyList<ServiceAccountDto>?>(page, "_accounts").Should().BeNull();
    }

    [Fact]
    public async Task RotationAndStatus_SendVersionFromConfirmedRow()
    {
        var original = ServiceAccountsApiClientTests.Account();
        var rotated = original with { Version = original.Version + 1, UpdatedAtUtc = original.UpdatedAtUtc.AddSeconds(1) };
        var disabled = rotated with { Version = rotated.Version + 1, IsActive = false, UpdatedAtUtc = rotated.UpdatedAtUtc.AddSeconds(1) };
        var listFails = false;
        using var page = Page(new StubGateway
        {
            Rotate = (id, input, _) => { id.Should().Be(original.Id); input.Version.Should().Be(original.Version); return Task.FromResult(new ServiceAccountKeyResultDto(rotated, ServiceAccountsApiClientTests.Key(rotated), 2)); },
            Active = (id, input, _) => { id.Should().Be(rotated.Id); input.Version.Should().Be(rotated.Version); input.IsActive.Should().BeFalse(); listFails = true; return Task.FromResult(new ServiceAccountActivityResultDto(disabled, 2)); },
            List = _ => listFails ? throw new ApiException("remote-secret", HttpStatusCode.ServiceUnavailable) : Task.FromResult<IReadOnlyList<ServiceAccountDto>>([rotated])
        });
        Set(page, "_accounts", new[] { original });
        Invoke(page, "AskConfirmation", original, true);
        await InvokeAsync(page, "ConfirmAsync");
        Get<string>(page, "_apiKey").Should().Be(ServiceAccountsApiClientTests.Key(rotated));
        Get<int>(page, "_revokedSessions").Should().Be(2);
        Invoke(page, "CloseKey");
        Invoke(page, "AskConfirmation", rotated, false);
        await InvokeAsync(page, "ConfirmAsync");
        Get<IReadOnlyList<ServiceAccountDto>>(page, "_accounts").Should().Equal(disabled);
        Get<string>(page, "_notice").Should().Contain("2");
        Get<string>(page, "_listError").Should().NotContain("remote-secret");
    }

    [Fact]
    public async Task LateCopyCompletion_DoesNotRestoreDismissedSecretState()
    {
        var account = ServiceAccountsApiClientTests.Account();
        var js = new ControlledJs();
        using var page = Page(new StubGateway(), js);
        Invoke(page, "ShowKey", new ServiceAccountKeyResultDto(account, ServiceAccountsApiClientTests.Key(account), 0));
        var copying = InvokeAsync(page, "CopyKeyAsync");
        Invoke(page, "CloseKey");
        js.Release.SetResult();
        await copying;
        Get<string?>(page, "_apiKey").Should().BeNull();
        Get<bool>(page, "_copied").Should().BeFalse();
    }

    [Fact]
    public async Task OlderList_CannotOverwriteResultOfNewerLoad()
    {
        var account = ServiceAccountsApiClientTests.Account();
        var updated = account with { Name = "Новое имя" };
        var stale = new TaskCompletionSource<IReadOnlyList<ServiceAccountDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var page = Page(new StubGateway { List = _ => ++calls == 1 ? stale.Task : Task.FromResult<IReadOnlyList<ServiceAccountDto>>([updated]) });
        var old = InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "LoadAsync");
        stale.SetResult([account]);
        await old;
        Get<IReadOnlyList<ServiceAccountDto>>(page, "_accounts").Should().Equal(updated);
    }

    [Fact]
    public async Task FailureMessages_DoNotReflectRemoteSecretsAndDoNotRetry()
    {
        var account = ServiceAccountsApiClientTests.Account();
        var key = ServiceAccountsApiClientTests.Key(account);
        var calls = 0;
        using var page = Page(new StubGateway { Create = (_, _) => { calls++; throw new ApiException(key, HttpStatusCode.Conflict); } });
        Invoke(page, "BeginCreate");
        Get<ServiceAccountForm>(page, "_form").Name = account.Name;
        await InvokeAsync(page, "SaveAsync");
        calls.Should().Be(1);
        Get<string>(page, "_formError").Should().NotContain(key).And.Contain("Обновите список");
        Get<string?>(page, "_apiKey").Should().BeNull();
    }

    [Fact]
    public async Task RenderedPage_UsesSeparateAccountsAndSecondPrecisionWithoutSecretOrDelete()
    {
        var account = ServiceAccountsApiClientTests.Account();
        var inactive = account with { Id = Guid.NewGuid(), Name = "Резервная LIS", IsActive = false };
        var services = new ServiceCollection().AddLogging().AddSingleton<IServiceAccountsGateway>(new StubGateway
            { List = _ => Task.FromResult<IReadOnlyList<ServiceAccountDto>>([account, inactive]) })
            .AddSingleton(Mock.Of<IJSRuntime>());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode(
            (await renderer.RenderComponentAsync<ServiceAccounts>(ParameterView.Empty)).ToHtmlString()));
        html.Should().Contain("Сервисные аккаунты").And.Contain("Заменить ключ").And.Contain("Отключён")
            .And.Contain(account.CreatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))
            .And.NotContain(ServiceAccountsApiClientTests.Key(account)).And.NotContain("Удалить");
    }

    [Fact]
    public void FormAndServiceRevocationLabel_RespectUnicodeBoundariesAndZero()
    {
        var unicode = string.Concat(Enumerable.Repeat("🧪", 64));
        var form = new ServiceAccountForm { Name = unicode };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), results, true).Should().BeTrue();
        form.Name = unicode + "a";
        Validator.TryValidateObject(form, new ValidationContext(form), results, true).Should().BeFalse();
        var metadata = new Dictionary<string, JsonElement> { ["revokedServiceSessionCount"] = JsonSerializer.SerializeToElement(0) };
        AuditLabels.RefreshRevocationSummary(metadata).Should().Be("Отозвано сервисных сессий: 0.");
        metadata = new Dictionary<string, JsonElement> { ["revokedRefreshTokenCount"] = JsonSerializer.SerializeToElement(2) };
        AuditLabels.RefreshRevocationSummary(metadata).Should().Be("Отозвано refresh-токенов: 2.");
    }

    private sealed class ControlledJs : IJSRuntime
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        { await Release.Task.WaitAsync(token); return default!; }
    }
    private static ServiceAccounts Page(IServiceAccountsGateway gateway, IJSRuntime? js = null)
    {
        var page = new ServiceAccounts();
        page.GetType().GetProperty("Gateway", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, gateway);
        page.GetType().GetProperty("Js", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, js ?? Mock.Of<IJSRuntime>());
        Set(page, "_cts", new CancellationTokenSource());
        return page;
    }
    private static void Set(object page, string name, object? value) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T Get<T>(object page, string name) => (T)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static object? Invoke(object page, string name, params object[] args) => page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static Task InvokeAsync(object page, string name, params object[] args) => (Task)Invoke(page, name, args)!;

    private sealed class StubGateway : IServiceAccountsGateway
    {
        public Func<CancellationToken, Task<IReadOnlyList<ServiceAccountDto>>>? List { get; init; }
        public Func<CreateServiceAccountRequest, CancellationToken, Task<ServiceAccountKeyResultDto>>? Create { get; init; }
        public Func<Guid, UpdateServiceAccountRequest, CancellationToken, Task<ServiceAccountDto>>? Update { get; init; }
        public Func<Guid, SetServiceAccountActiveRequest, CancellationToken, Task<ServiceAccountActivityResultDto>>? Active { get; init; }
        public Func<Guid, RotateServiceAccountKeyRequest, CancellationToken, Task<ServiceAccountKeyResultDto>>? Rotate { get; init; }
        public Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken token) => List!(token);
        public Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest request, CancellationToken token) => Create!(request, token);
        public Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest request, CancellationToken token) => Update!(id, request, token);
        public Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id, SetServiceAccountActiveRequest request, CancellationToken token) => Active!(id, request, token);
        public Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest request, CancellationToken token) => Rotate!(id, request, token);
    }
}
