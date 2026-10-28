using System.Net;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Components.Authentication;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class AdministratorLoginPageTests
{
    [Fact]
    public async Task Render_AdministratorPasswordStep_ShouldShowContinueWithoutTotpInput()
    {
        var gateway = Gateway();
        var html = await RenderAsync<LoginForm>(gateway.Object,
            new Dictionary<string, object?> { [nameof(LoginForm.Portal)] = LoginPortal.Administrator });

        html.Should().Contain("Продолжить").And.Contain("id=\"password\"").And.NotContain("id=\"totp\"");
    }

    [Fact]
    public async Task Login_ValidAdministratorPassword_ShouldNavigateToSeparatePageAndClearPassword()
    {
        const string password = "Synthetic-Admin-Password1";
        var gateway = Gateway();
        gateway.Setup(value => value.BeginAdministratorLoginAsync(
                It.Is<BeginAdministratorLoginRequest>(request => request.UserName == "admin" && request.Password == password),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdministratorLoginChallengeDto("admin", DateTimeOffset.UtcNow.AddMinutes(5)));
        var navigation = new TestNavigation();
        using var form = Create<LoginForm>(gateway.Object, navigation);
        typeof(LoginForm).GetProperty(nameof(LoginForm.Portal))!.SetValue(form, LoginPortal.Administrator);
        typeof(LoginForm).GetProperty(nameof(LoginForm.ReturnUrl))!.SetValue(form, "/admin/accounts");
        SetForm(form, "UserName", "admin");
        SetForm(form, "Password", password);

        await InvokeAsync(form, "LoginAsync");

        navigation.Uri.Should().Contain("/admin/login/confirm?returnUrl=").And.NotContain(password);
        GetForm(form, "Password").Should().BeEmpty();
        gateway.Verify(value => value.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Login_IncorrectAdministratorPassword_ShouldStayOnPasswordPageAndClearPassword()
    {
        var gateway = Gateway();
        gateway.Setup(value => value.BeginAdministratorLoginAsync(It.IsAny<BeginAdministratorLoginRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Incorrect credentials", HttpStatusCode.Unauthorized));
        var navigation = new TestNavigation();
        using var form = Create<LoginForm>(gateway.Object, navigation);
        typeof(LoginForm).GetProperty(nameof(LoginForm.Portal))!.SetValue(form, LoginPortal.Administrator);
        SetForm(form, "UserName", "admin");
        SetForm(form, "Password", "Wrong-Password1");

        await InvokeAsync(form, "LoginAsync");

        navigation.Uri.Should().EndWith("/admin/login");
        GetForm(form, "Password").Should().BeEmpty();
        Get<string?>(form, "_error").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Render_ConfirmationStep_ShouldLoadChallengeWithoutPasswordField()
    {
        var gateway = Gateway();
        var html = await RenderAsync<AdministratorLoginConfirmation>(gateway.Object);

        html.Should().Contain("Пароль подтверждён").And.Contain("admin").And.Contain("id=\"totp\"")
            .And.NotContain("type=\"password\"");
        gateway.Verify(value => value.GetAdministratorLoginChallengeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Load_MissingChallenge_ShouldReturnToPasswordStep()
    {
        var gateway = Gateway();
        gateway.Setup(value => value.GetAdministratorLoginChallengeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Missing challenge", HttpStatusCode.Unauthorized));
        var navigation = new TestNavigation();
        using var page = Create<AdministratorLoginConfirmation>(gateway.Object, navigation);

        await InvokeAsync(page, "LoadAsync");

        new Uri(navigation.Uri).AbsolutePath.Should().Be("/admin/login");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_PageDisposedBeforeResponse_ShouldNotOverrideNewNavigation(bool unauthorized)
    {
        var completion = new TaskCompletionSource<AdministratorLoginChallengeDto>();
        var gateway = Gateway();
        gateway.Setup(value => value.GetAdministratorLoginChallengeAsync(It.IsAny<CancellationToken>()))
            .Returns(completion.Task);
        var navigation = new TestNavigation();
        var page = Create<AdministratorLoginConfirmation>(gateway.Object, navigation);
        var pending = InvokeAsync(page, "LoadAsync");
        page.Dispose();
        navigation.NavigateTo("/login");
        if (unauthorized) completion.SetException(new ApiException("Expired", HttpStatusCode.Unauthorized));
        else completion.SetResult(new AdministratorLoginChallengeDto("admin", DateTimeOffset.UnixEpoch));

        await pending;

        new Uri(navigation.Uri).AbsolutePath.Should().Be("/login");
    }

    [Fact]
    public async Task Confirm_CorrectCode_ShouldCompleteLoginAndClearCode()
    {
        var gateway = Gateway();
        gateway.Setup(value => value.ConfirmAdministratorLoginAsync(
                It.Is<ConfirmAdministratorLoginRequest>(request => request.Code == "123456"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticationSessionDto(Guid.NewGuid(), "admin", ApplicationRoles.Administrator, null, null));
        var navigation = new TestNavigation();
        using var page = Create<AdministratorLoginConfirmation>(gateway.Object, navigation);
        page.ReturnUrl = "/admin/accounts";
        await InvokeAsync(page, "LoadAsync");
        SetForm(page, "Code", "123456");

        await InvokeAsync(page, "ConfirmAsync");

        navigation.Uri.Should().EndWith("/admin/accounts");
        GetForm(page, "Code").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_IncorrectCode_ShouldKeepChallengeAndAllowRetryWithoutKeepingCode()
    {
        var gateway = Gateway();
        gateway.Setup(value => value.ConfirmAdministratorLoginAsync(It.IsAny<ConfirmAdministratorLoginRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Incorrect code", HttpStatusCode.Unauthorized));
        var navigation = new TestNavigation();
        using var page = Create<AdministratorLoginConfirmation>(gateway.Object, navigation);
        await InvokeAsync(page, "LoadAsync");
        SetForm(page, "Code", "000000");

        await InvokeAsync(page, "ConfirmAsync");

        Get<AdministratorLoginChallengeDto?>(page, "_challenge").Should().NotBeNull();
        GetForm(page, "Code").Should().BeEmpty();
        Get<bool>(page, "_busy").Should().BeFalse();
        Get<string?>(page, "_error").Should().Contain("Код не подошёл");
    }

    private static Mock<IAuthenticationGateway> Gateway()
    {
        var gateway = new Mock<IAuthenticationGateway>();
        gateway.Setup(value => value.GetSetupStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetupStatusDto(false));
        gateway.Setup(value => value.GetAdministratorLoginChallengeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdministratorLoginChallengeDto("admin", DateTimeOffset.UtcNow.AddMinutes(5)));
        return gateway;
    }

    private static T Create<T>(IAuthenticationGateway gateway, NavigationManager navigation) where T : new()
    {
        var component = new T();
        foreach (var (name, value) in new (string, object)[] { ("Authentication", gateway), ("Navigation", navigation) })
            typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);
        return component;
    }

    private static async Task<string> RenderAsync<T>(IAuthenticationGateway gateway,
        Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(gateway);
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddSingleton<IJSRuntime>(new Mock<IJSRuntime>().Object);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(
                ParameterView.FromDictionary(parameters ?? new Dictionary<string, object?>()));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    private static T Get<T>(object component, string name) => (T)component.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
    private static void SetForm(object component, string property, string value) =>
        Get<object>(component, "_form").GetType().GetProperty(property)!.SetValue(Get<object>(component, "_form"), value);
    private static string GetForm(object component, string property) =>
        (string)Get<object>(component, "_form").GetType().GetProperty(property)!.GetValue(Get<object>(component, "_form"))!;
    private static Task InvokeAsync(object component, string method) =>
        (Task)component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/admin/login");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
