using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.ServiceAccounts;
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
    [Theory]
    [InlineData(ServiceAccountRoles.Lis)]
    [InlineData(ServiceAccountRoles.Hl7Import)]
    public async Task Create_SendsSelectedRoleAndKeepsRoleWithOneTimeKey(string role)
    {
        var account = ServiceAccountsApiClientTests.Account() with { Role = role };
        using var page = Page(new StubGateway
        {
            Create = (input, _) =>
            {
                input.Role.Should().Be(role);
                return Task.FromResult(new ServiceAccountKeyResultDto(account, ServiceAccountsApiClientTests.Key(account), 0));
            },
            List = _ => Task.FromResult<IReadOnlyList<ServiceAccountDto>>([account])
        });
        Invoke(page, "BeginCreate");
        var form = Get<ServiceAccountForm>(page, "_form");
        form.Role.Should().Be(ServiceAccountRoles.Lis);
        form.Name = account.Name;
        form.Role = role;

        await InvokeAsync(page, "SaveAsync");

        Get<IReadOnlyList<ServiceAccountDto>>(page, "_accounts").Single().Role.Should().Be(role);
        Get<string>(page, "_secretAccountRole").Should().Be(role);
        Invoke(page, "CloseKey");
        Get<string?>(page, "_secretAccountRole").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hl7Import")]
    [InlineData("Administrator")]
    public async Task Create_RejectsUnsupportedRoleBeforeGateway(string? role)
    {
        var calls = 0;
        using var page = Page(new StubGateway { Create = (_, _) => { calls++; throw new InvalidOperationException(); } });
        Invoke(page, "BeginCreate");
        var form = Get<ServiceAccountForm>(page, "_form");
        form.Name = "Импорт пациентов";
        form.Role = role!;
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), results, true).Should().BeFalse();
        results.Should().Contain(result => result.MemberNames.Contains(nameof(ServiceAccountForm.Role)));

        await InvokeAsync(page, "SaveAsync");

        calls.Should().Be(0);
        Get<bool>(page, "_formOpen").Should().BeTrue();
    }

    [Theory]
    [InlineData(ServiceAccountRoles.Lis, false)]
    [InlineData(ServiceAccountRoles.Lis, true)]
    [InlineData(ServiceAccountRoles.Hl7Import, false)]
    [InlineData(ServiceAccountRoles.Hl7Import, true)]
    public async Task RenderedForm_ShowsRolePermissionsAndCreationOnlySelector(string role, bool editing)
    {
        var account = ServiceAccountsApiClientTests.Account() with { Role = role };
        var services = new ServiceCollection().AddLogging().AddSingleton<IServiceAccountsGateway>(new StubGateway
        { List = _ => Task.FromResult<IReadOnlyList<ServiceAccountDto>>([account]) })
            .AddSingleton(Mock.Of<IJSRuntime>());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(OpenFormServiceAccounts.Role)] = role,
            [nameof(OpenFormServiceAccounts.Editing)] = editing
        });
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode(
            (await renderer.RenderComponentAsync<OpenFormServiceAccounts>(parameters)).ToHtmlString()));
        var start = html.IndexOf("<section class=\"service-permissions\"", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        var permissions = html[start..end];
        permissions.Should().Contain("POST /api/service-auth/token").And.Contain("POST /api/service-auth/refresh")
            .And.Contain("GET /api/testConnection").And.NotContain("<input").And.NotContain("<select");
        if (role == ServiceAccountRoles.Hl7Import)
        {
            permissions.Should().Contain("Разрешения роли HL7 Import").And.Contain("MLLP / mTLS")
                .And.NotContain("Чтение FHIR").And.NotContain("POST /fhir/Observation");
            html.Should().Contain(">HL7 Import</span>").And.NotContain(">HL7Import<");
        }
        else
        {
            permissions.Should().Contain("Patient, Practitioner, ServiceRequest, Observation")
                .And.Contain("POST /fhir/Observation").And.NotContain("MLLP / mTLS");
        }
        if (editing)
            html.Should().Contain("readonly").And.Contain("Роль задана при создании аккаунта.")
                .And.NotMatchRegex("<select[^>]*id=\"service-account-role\"");
        else
            html.Should().MatchRegex("<select[^>]*id=\"service-account-role\"")
                .And.Contain("value=\"HL7Import\"").And.Contain("Роль выбирается при создании аккаунта.");
    }

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
    public async Task RenameConflict_PreservesDraftUntilExplicitReloadAndHandlesAnotherConflict()
    {
        var original = ServiceAccountsApiClientTests.Account();
        var current = original with { Name = "Имя другого администратора", Version = original.Version + 1 };
        var writes = 0;
        var reads = 0;
        using var page = Page(new StubGateway
        {
            List = _ => Task.FromResult<IReadOnlyList<ServiceAccountDto>>([current]),
            Get = (id, _) => { id.Should().Be(original.Id); reads++; return Task.FromResult(current); },
            Update = (id, input, _) =>
            {
                id.Should().Be(original.Id);
                writes++;
                if (writes == 1)
                {
                    input.Version.Should().Be(original.Version);
                    throw new ApiException("remote-secret", HttpStatusCode.Conflict);
                }
                input.Version.Should().Be(current.Version);
                if (writes == 2)
                {
                    current = current with { Name = "Ещё одно параллельное изменение", Version = current.Version + 1 };
                    throw new ApiException("remote-secret", HttpStatusCode.Conflict);
                }
                current = current with { Name = input.Name, Version = current.Version + 1 };
                return Task.FromResult(current);
            }
        });
        Invoke(page, "BeginEdit", original);
        var draft = Get<ServiceAccountForm>(page, "_form");
        draft.Name = "Моё новое имя";

        await InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "SaveAsync");

        writes.Should().Be(1);
        reads.Should().Be(0);
        Get<bool>(page, "_formStale").Should().BeTrue();
        Get<ServiceAccountDto>(page, "_editing").Should().Be(original);
        Get<ServiceAccountForm>(page, "_form").Should().BeSameAs(draft);
        draft.Name.Should().Be("Моё новое имя");
        Get<string>(page, "_formError").Should().NotContain("remote-secret");

        await InvokeAsync(page, "ReloadEditingAsync");

        writes.Should().Be(1);
        reads.Should().Be(1);
        Get<ServiceAccountForm>(page, "_form").Name.Should().Be(current.Name);
        Get<ServiceAccountDto>(page, "_editing").Version.Should().Be(current.Version);
        Get<bool>(page, "_formStale").Should().BeFalse();
        Get<ServiceAccountForm>(page, "_form").Name = "Правка после загрузки";
        await InvokeAsync(page, "SaveAsync");
        Get<bool>(page, "_formStale").Should().BeTrue();
        Get<ServiceAccountForm>(page, "_form").Name.Should().Be("Правка после загрузки");
        await InvokeAsync(page, "SaveAsync");
        writes.Should().Be(2);

        await InvokeAsync(page, "ReloadEditingAsync");
        Get<ServiceAccountForm>(page, "_form").Name = "Подтверждённое имя";
        await InvokeAsync(page, "SaveAsync");

        writes.Should().Be(3);
        reads.Should().Be(2);
        Get<bool>(page, "_formOpen").Should().BeFalse();
        Get<IReadOnlyList<ServiceAccountDto>>(page, "_accounts").Single().Name.Should().Be("Подтверждённое имя");
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public async Task ConfirmationConflict_RequiresFreshReadAndExplicitConfirmationWithoutReversingActivity(
        bool rotate, bool originallyActive, bool alreadyApplied)
    {
        var original = ServiceAccountsApiClientTests.Account() with { IsActive = originallyActive };
        var current = original with
        {
            Name = "Актуальная карточка",
            Version = original.Version + 1,
            IsActive = alreadyApplied ? !originallyActive : originallyActive,
            UpdatedAtUtc = original.UpdatedAtUtc.AddSeconds(1)
        };
        var writes = 0;
        var reads = 0;
        var gateway = new StubGateway
        {
            List = _ => Task.FromResult<IReadOnlyList<ServiceAccountDto>>([current]),
            Get = (id, _) => { id.Should().Be(original.Id); reads++; return Task.FromResult(current); },
            Rotate = (id, input, _) =>
            {
                id.Should().Be(original.Id);
                writes++;
                if (writes == 1)
                {
                    input.Version.Should().Be(original.Version);
                    throw new ApiException("remote-secret", HttpStatusCode.Conflict);
                }
                input.Version.Should().Be(current.Version);
                if (writes == 2)
                {
                    current = current with { Name = "Карточка снова изменена", Version = current.Version + 1 };
                    throw new ApiException("remote-secret", HttpStatusCode.Conflict);
                }
                current = current with { Version = current.Version + 1 };
                return Task.FromResult(new ServiceAccountKeyResultDto(current, ServiceAccountsApiClientTests.Key(current), 0));
            },
            Active = (id, input, _) =>
            {
                id.Should().Be(original.Id);
                input.IsActive.Should().Be(!originallyActive);
                writes++;
                if (writes == 1)
                {
                    input.Version.Should().Be(original.Version);
                    throw new ApiException("remote-secret", HttpStatusCode.Conflict);
                }
                input.Version.Should().Be(current.Version);
                if (writes == 2)
                {
                    current = current with { Name = "Карточка снова изменена", Version = current.Version + 1 };
                    throw new ApiException("remote-secret", HttpStatusCode.Conflict);
                }
                current = current with { Version = current.Version + 1, IsActive = input.IsActive };
                return Task.FromResult(new ServiceAccountActivityResultDto(current, 0));
            }
        };
        using var page = Page(gateway);
        Invoke(page, "AskConfirmation", original, rotate);

        await InvokeAsync(page, "ConfirmAsync");
        await InvokeAsync(page, "LoadAsync");
        await InvokeAsync(page, "ConfirmAsync");

        writes.Should().Be(1);
        reads.Should().Be(0);
        Get<ServiceAccountDto>(page, "_pending").Should().Be(original);
        Get<bool>(page, "_pendingStale").Should().BeTrue();
        Get<string>(page, "_operationError").Should().NotContain("remote-secret");
        Get<string?>(page, "_apiKey").Should().BeNull();

        await InvokeAsync(page, "ReloadPendingAsync");

        reads.Should().Be(1);
        writes.Should().Be(1);
        Get<bool>(page, "_pendingStale").Should().BeFalse();
        if (alreadyApplied)
        {
            Get<ServiceAccountDto?>(page, "_pending").Should().BeNull();
            await InvokeAsync(page, "ConfirmAsync");
            writes.Should().Be(1);
        }
        else
        {
            Get<ServiceAccountDto>(page, "_pending").Should().Be(current);
            await InvokeAsync(page, "ConfirmAsync");
            writes.Should().Be(2);
            Get<bool>(page, "_pendingStale").Should().BeTrue();
            await InvokeAsync(page, "ConfirmAsync");
            writes.Should().Be(2);
            await InvokeAsync(page, "ReloadPendingAsync");
            Get<ServiceAccountDto>(page, "_pending").Should().Be(current);
            writes.Should().Be(2);
            reads.Should().Be(2);
            await InvokeAsync(page, "ConfirmAsync");
            writes.Should().Be(3);
            Get<ServiceAccountDto?>(page, "_pending").Should().BeNull();
            Get<string?>(page, "_apiKey").Should().Be(rotate ? ServiceAccountsApiClientTests.Key(current) : null);
        }
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("rotate")]
    [InlineData("active")]
    public async Task FailedReload_KeepsOriginalDraftOrConfirmationAndBlocksNewMutation(string operation)
    {
        var original = ServiceAccountsApiClientTests.Account();
        var writes = 0;
        using var page = Page(new StubGateway
        {
            Update = (_, _, _) => { writes++; throw new ApiException("remote-secret", HttpStatusCode.Conflict); },
            Rotate = (_, _, _) => { writes++; throw new ApiException("remote-secret", HttpStatusCode.Conflict); },
            Active = (_, _, _) => { writes++; throw new ApiException("remote-secret", HttpStatusCode.Conflict); },
            Get = (_, _) => throw new HttpRequestException("remote-secret")
        });
        if (operation == "rename")
        {
            Invoke(page, "BeginEdit", original);
            var draft = Get<ServiceAccountForm>(page, "_form");
            draft.Name = "Несохранённый ввод";
            await InvokeAsync(page, "SaveAsync");
            await InvokeAsync(page, "ReloadEditingAsync");
            await InvokeAsync(page, "SaveAsync");

            Get<ServiceAccountForm>(page, "_form").Should().BeSameAs(draft);
            draft.Name.Should().Be("Несохранённый ввод");
            Get<ServiceAccountDto>(page, "_editing").Should().Be(original);
            Get<bool>(page, "_formStale").Should().BeTrue();
            Get<string>(page, "_formError").Should().Contain("Повторите загрузку").And.NotContain("remote-secret");
        }
        else
        {
            Invoke(page, "AskConfirmation", original, operation == "rotate");
            await InvokeAsync(page, "ConfirmAsync");
            await InvokeAsync(page, "ReloadPendingAsync");
            await InvokeAsync(page, "ConfirmAsync");

            Get<ServiceAccountDto>(page, "_pending").Should().Be(original);
            Get<bool>(page, "_pendingStale").Should().BeTrue();
            Get<string>(page, "_operationError").Should().Contain("Повторите загрузку").And.NotContain("remote-secret");
        }
        writes.Should().Be(1);
        Get<bool>(page, "_reloading").Should().BeFalse();
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
    private static void Set(object page, string name, object? value) => typeof(ServiceAccounts).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T Get<T>(object page, string name) => (T)typeof(ServiceAccounts).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static object? Invoke(object page, string name, params object[] args) => typeof(ServiceAccounts).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static Task InvokeAsync(object page, string name, params object[] args) => (Task)Invoke(page, name, args)!;

    private sealed class OpenFormServiceAccounts : ServiceAccounts
    {
        [Parameter] public string Role { get; set; } = ServiceAccountRoles.Lis;
        [Parameter] public bool Editing { get; set; }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            if (Editing)
                Invoke(this, "BeginEdit", Get<IReadOnlyList<ServiceAccountDto>>(this, "_accounts").Single());
            else
            {
                Invoke(this, "BeginCreate");
                Get<ServiceAccountForm>(this, "_form").Role = Role;
            }
        }
    }

    private sealed class StubGateway : IServiceAccountsGateway
    {
        public Func<CancellationToken, Task<IReadOnlyList<ServiceAccountDto>>>? List { get; init; }
        public Func<Guid, CancellationToken, Task<ServiceAccountDto>>? Get { get; init; }
        public Func<CreateServiceAccountRequest, CancellationToken, Task<ServiceAccountKeyResultDto>>? Create { get; init; }
        public Func<Guid, UpdateServiceAccountRequest, CancellationToken, Task<ServiceAccountDto>>? Update { get; init; }
        public Func<Guid, SetServiceAccountActiveRequest, CancellationToken, Task<ServiceAccountActivityResultDto>>? Active { get; init; }
        public Func<Guid, RotateServiceAccountKeyRequest, CancellationToken, Task<ServiceAccountKeyResultDto>>? Rotate { get; init; }
        public Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken token) => List!(token);
        public Task<ServiceAccountDto> GetAsync(Guid id, CancellationToken token) => Get!(id, token);
        public Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest request, CancellationToken token) => Create!(request, token);
        public Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest request, CancellationToken token) => Update!(id, request, token);
        public Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id, SetServiceAccountActiveRequest request, CancellationToken token) => Active!(id, request, token);
        public Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest request, CancellationToken token) => Rotate!(id, request, token);
    }
}
