using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Components.Accounts;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MedicalWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class AccountUiLifecycleTests
{
    [Fact]
    public async Task LoadAccounts_DisposedDuringList_ShouldStopBeforeLoadingWorkers()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<AccountDetailsDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IAccountManagementGateway>();
        gateway.Setup(service => service.ListAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var http = new HttpClient(new ResponseHandler((_, _) =>
            throw new InvalidOperationException("Workers must not load after page disposal.")))
        { BaseAddress = new Uri("https://localhost/") };
        var page = new Accounts();
        Inject(page, "Gateway", gateway.Object);
        Inject(page, "Workers", new MedicalWorkersApiClient(http));

        var load = InvokeAsync(page, "LoadAsync");
        page.Dispose();
        pending.SetResult([Account(Guid.NewGuid())]);
        await load;

        Field(page, "_accounts").Should().BeNull();
        await InvokeAsync(page, "LoadAsync");
        gateway.Verify(service => service.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadAccounts_OperationCompletesDuringRefresh_ShouldPreserveConfirmedAccountChange()
    {
        var original = Account(Guid.NewGuid());
        var updated = original with { IsActive = false, UpdatedAt = original.UpdatedAt.AddMinutes(1) };
        var pending = new TaskCompletionSource<IReadOnlyList<AccountDetailsDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IAccountManagementGateway>();
        gateway.Setup(service => service.ListAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var http = new HttpClient(new ResponseHandler((_, _) =>
            throw new InvalidOperationException("An obsolete list must not continue loading.")))
        { BaseAddress = new Uri("https://localhost/") };
        using var page = new Accounts();
        Inject(page, "Gateway", gateway.Object);
        Inject(page, "Workers", new MedicalWorkersApiClient(http));
        page.GetType().GetField("_accounts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(page, new[] { original });
        var load = InvokeAsync(page, "LoadAsync");

        Invoke(page, "OnAccountChanged", updated);
        pending.SetResult([original]);
        await load;

        Get<IReadOnlyList<AccountDetailsDto>>(page, "_accounts").Should().Equal(updated);
        Get<bool>(page, "_loading").Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_RefreshedAccount_ShouldShowNewPasswordAndPublishServerMetadata()
    {
        var original = Account(Guid.NewGuid()) with { IsLockedOut = true };
        var updated = original with { IsLockedOut = false, UpdatedAt = original.UpdatedAt.AddMinutes(1) };
        var gateway = new Mock<IAccountManagementGateway>();
        gateway.Setup(service => service.ResetPasswordAsync(original.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetAccountPasswordResultDto(original.Id, "NewDemoPass12", 2));
        gateway.Setup(service => service.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { updated });
        using var operations = new AccountOperations();
        Inject(operations, "Accounts", gateway.Object);
        SetProperty(operations, "Account", original);
        var changes = new List<AccountDetailsDto>();
        SetProperty(operations, "OnChanged", EventCallback.Factory.Create<AccountDetailsDto>(changes,
            (Action<AccountDetailsDto>)changes.Add));

        await InvokeAsync(operations, "ResetAsync");

        Get<AccountDetailsDto>(operations, "Account", property: true).Should().Be(updated);
        Get<string>(operations, "_generatedPassword").Should().Be("NewDemoPass12");
        changes.Should().NotBeEmpty().And.OnlyContain(account => !account.IsLockedOut);
        changes.Last().Should().Be(updated);
        Get<string>(operations, "_notice").Should().Contain("2");
        Field(operations, "_error").Should().BeNull();
    }

    [Fact]
    public async Task ResetPassword_MetadataRefreshFails_ShouldKeepSuccessfulResultAndNewPassword()
    {
        var original = Account(Guid.NewGuid()) with { IsLockedOut = true };
        var gateway = new Mock<IAccountManagementGateway>();
        gateway.Setup(service => service.ResetPasswordAsync(original.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetAccountPasswordResultDto(original.Id, "NewDemoPass12", 3));
        gateway.Setup(service => service.ListAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Synthetic metadata failure."));
        using var operations = new AccountOperations();
        Inject(operations, "Accounts", gateway.Object);
        SetProperty(operations, "Account", original);

        await InvokeAsync(operations, "ResetAsync");

        Get<string>(operations, "_generatedPassword").Should().Be("NewDemoPass12");
        Get<AccountDetailsDto>(operations, "Account", property: true).IsLockedOut.Should().BeFalse();
        Get<string>(operations, "_notice").Should().Contain("Пароль обновлён").And.Contain("не удалось");
        Field(operations, "_error").Should().BeNull();
        Field(operations, "_confirm").Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAccount_FollowedByAccountMutation_ShouldHidePreviousGeneratedPassword(bool reset)
    {
        var workerId = Guid.NewGuid();
        var account = Account(workerId);
        var updated = account with { UpdatedAt = account.UpdatedAt.AddMinutes(1) };
        var created = false;
        var gateway = new Mock<IAccountManagementGateway>();
        gateway.Setup(service => service.ListAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IReadOnlyList<AccountDetailsDto>>(created ? [updated] : []));
        gateway.Setup(service => service.CreateForWorkerAsync(workerId,
                It.IsAny<CreateWorkerAccountRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => created = true)
            .ReturnsAsync(new CreateWorkerAccountResultDto(account, "CreatedPass12"));
        gateway.Setup(service => service.ResetPasswordAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetAccountPasswordResultDto(account.Id, "NewDemoPass12", 1));
        gateway.Setup(service => service.SetActiveAsync(account.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetAccountActiveResultDto(account with { IsActive = false }, 1));
        var activator = new CaptureActivator();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(gateway.Object);
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<WorkerAccountPanel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkerAccountPanel.WorkerId)] = workerId }));
            var panel = activator.Components.OfType<WorkerAccountPanel>().Single();
            var form = Field(panel, "_form")!;
            SetProperty(form, "UserName", "doctor.demo");
            await InvokeAsync(panel, "CreateAsync");
            Rerender(panel);
            output.ToHtmlString().Should().Contain("CreatedPass12");
            var operations = activator.Components.OfType<AccountOperations>().Single();

            if (reset) await InvokeAsync(operations, "ResetAsync");
            else await InvokeAsync(operations, "SetActiveAsync", false);
            Rerender(operations);
            return output.ToHtmlString();
        });

        html.Should().NotContain("CreatedPass12");
        if (reset)
        {
            html.Should().Contain("NewDemoPass12");
            html.Split("class=\"surface password-card\"").Should().HaveCount(2);
        }
        else html.Should().NotContain("surface password-card");
    }

    [Fact]
    public async Task LoadWorker_ChangedDuringCreate_ShouldClearOldStateAndIgnoreLatePassword()
    {
        var firstWorker = Guid.NewGuid();
        var secondWorker = Guid.NewGuid();
        var pending = new TaskCompletionSource<CreateWorkerAccountResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IAccountManagementGateway>();
        gateway.Setup(service => service.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountDetailsDto>());
        gateway.Setup(service => service.CreateForWorkerAsync(firstWorker,
                It.IsAny<CreateWorkerAccountRequest>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var panel = new WorkerAccountPanel();
        Inject(panel, "Accounts", gateway.Object);
        SetProperty(panel, "WorkerId", firstWorker);
        await InvokeAsync(panel, "OnParametersSetAsync");
        SetProperty(Field(panel, "_form")!, "UserName", "first.worker");
        var create = InvokeAsync(panel, "CreateAsync");
        Get<bool>(panel, "_creating").Should().BeTrue();

        SetProperty(panel, "WorkerId", secondWorker);
        await InvokeAsync(panel, "OnParametersSetAsync");
        pending.SetResult(new CreateWorkerAccountResultDto(Account(firstWorker), "StalePass123"));
        await create;

        Get<bool>(panel, "_creating").Should().BeFalse();
        Field(panel, "_account").Should().BeNull();
        Field(panel, "_createdPassword").Should().BeNull();
        Field(panel, "_createError").Should().BeNull();
        Get<string>(Field(panel, "_form")!, "UserName", property: true).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyPassword_PasswordChanges_ShouldRequireCopyingNewPassword(bool delayed)
    {
        var js = new ControlledJs(delayed);
        var card = new GeneratedPasswordCard();
        Inject(card, "Js", js);
        SetProperty(card, "Password", "OldDemoPass12");
        Invoke(card, "OnParametersSet");
        var copy = InvokeAsync(card, "CopyAsync", "OldDemoPass12");
        if (delayed) await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else
        {
            await copy;
            Get<bool>(card, "_copied").Should().BeTrue();
        }

        SetProperty(card, "Password", "NewDemoPass12");
        Invoke(card, "OnParametersSet");
        js.Release.TrySetResult();
        await copy;

        Get<bool>(card, "_copied").Should().BeFalse();
    }

    [Fact]
    public async Task LoadMedicalWorker_RepeatedSameRoute_ShouldPreserveUnsavedFormAndLoadNewRoute()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            requests++;
            var id = Guid.Parse(request.RequestUri!.Segments.Last());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new MedicalWorkerDto(id, id == first ? "Иван" : "Пётр",
                    "Иванов", "Врач", false, MedicalWorkerRole.Doctor))
            });
        }))
        { BaseAddress = new Uri("https://localhost/") };
        using var page = new MedicalWorkerEdit();
        Inject(page, "Api", new MedicalWorkersApiClient(http));
        SetProperty(page, "Id", first);
        await InvokeAsync(page, "OnParametersSetAsync");
        SetProperty(Field(page, "_model")!, "FirstName", "Несохранено");

        await InvokeAsync(page, "OnParametersSetAsync");

        requests.Should().Be(1);
        Get<string>(Field(page, "_model")!, "FirstName", property: true).Should().Be("Несохранено");
        SetProperty(page, "Id", second);
        await InvokeAsync(page, "OnParametersSetAsync");
        requests.Should().Be(2);
        Get<string>(Field(page, "_model")!, "FirstName", property: true).Should().Be("Пётр");
    }

    [Fact]
    public async Task LoadAudit_DisposedBeforeInitialResponse_ShouldNotPublishItemsOrStartPolling()
    {
        var pending = new TaskCompletionSource<PagedResult<AuditEventDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IAuditGateway>();
        gateway.Setup(service => service.ListAsync(It.IsAny<AuditQuery>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var page = new Audit();
        Inject(page, "Gateway", gateway.Object);
        var load = InvokeAsync(page, "OnInitializedAsync");
        page.Dispose();
        pending.SetResult(new PagedResult<AuditEventDto>([], 1, 50, false));

        await load;

        Get<IReadOnlyList<AuditEventDto>>(page, "_items").Should().BeEmpty();
        Field(page, "_pollTask").Should().BeNull();
        Field(page, "_timer").Should().BeNull();
        await InvokeAsync(page, "OnInitializedAsync");
        gateway.Verify(service => service.ListAsync(It.IsAny<AuditQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AccountDetailsDto Account(Guid workerId) => new(Guid.NewGuid(), "doctor.demo",
        ApplicationRoles.MedicalWorker, workerId, "Иван Иванов", MedicalWorkerRole.Doctor, true, false,
        new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static T Get<T>(object instance, string name, bool property = false) => property
        ? (T)instance.GetType().GetProperty(name)!.GetValue(instance)!
        : (T)Field(instance, name)!;
    private static void SetProperty(object instance, string name, object? value) =>
        instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static void Inject(object instance, string name, object value) =>
        instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, arguments);
    private static Task InvokeAsync(object instance, string name, params object[] arguments) =>
        (Task)Invoke(instance, name, arguments)!;
    private static void Rerender(ComponentBase component) => typeof(ComponentBase)
        .GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null);

    private sealed class CaptureActivator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            Components.Add(component);
            return component;
        }
    }

    private sealed class ControlledJs(bool delayed) : IJSRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier,
            CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            object?[]? args)
        {
            Entered.TrySetResult();
            if (delayed) await Release.Task.WaitAsync(cancellationToken);
            return default!;
        }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
