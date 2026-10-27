using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Gateways;
using HospitalInformationSystem.Web.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class SetupPageTests
{
    [Fact]
    public void Validate_ServerCompliantAlphanumericPassword_ShouldAcceptInput()
    {
        using var page = new Setup();
        FillCredentials(page);
        var form = Get<object>(page, "_credentials");
        var errors = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(form, new ValidationContext(form), errors, true);

        valid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Aa123456789", "Aa123456789")]
    [InlineData("Aa1234567890", "Different123")]
    public void Validate_InvalidPasswordOrConfirmation_ShouldRejectInput(string password, string confirmation)
    {
        using var page = new Setup();
        FillCredentials(page);
        SetForm(page, "_credentials", "Password", password);
        SetForm(page, "_credentials", "ConfirmPassword", confirmation);
        var form = Get<object>(page, "_credentials");

        Validator.TryValidateObject(form, new ValidationContext(form), [], true).Should().BeFalse();
    }

    [Fact]
    public async Task Begin_ValidChallenge_ShouldRetainExpiryAndClearPasswords()
    {
        var challenge = Challenge(DateTimeOffset.UtcNow.AddMinutes(5));
        var gateway = new StubAuthenticationGateway { Begin = (_, _, _) => Task.FromResult(challenge) };
        using var page = CreatePage(gateway);
        FillCredentials(page);

        await InvokeAsync(page, "BeginAsync");

        Get<Guid>(page, "_challengeId").Should().Be(challenge.ChallengeId);
        Get<DateTimeOffset?>(page, "_challengeExpiresAt").Should().Be(challenge.ExpiresAt);
        Get<string>(page, "_secret").Should().Be(challenge.SecretKey);
        Step(page).Should().Be("Totp");
        GetForm(page, "_credentials", "Password").Should().BeEmpty();
        GetForm(page, "_credentials", "ConfirmPassword").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_ExpiredChallenge_ShouldAllowNewAttempt()
    {
        var expired = Challenge(DateTimeOffset.UnixEpoch);
        var replacement = Challenge(DateTimeOffset.UtcNow.AddMinutes(5));
        var challenges = new Queue<SetupChallengeDto>([expired, replacement]);
        var gateway = new StubAuthenticationGateway
        {
            Begin = (_, _, _) => Task.FromResult(challenges.Dequeue()),
            Confirm = (request, _, _) => request.ChallengeId == expired.ChallengeId
                ? Task.FromException(new ApiException("Настройка истекла или исчерпаны попытки. Начните её заново.",
                    HttpStatusCode.BadRequest))
                : Task.CompletedTask
        };
        using var page = CreatePage(gateway);
        FillCredentials(page);
        await InvokeAsync(page, "BeginAsync");
        SetForm(page, "_confirmation", "Code", "123456");

        await InvokeAsync(page, "ConfirmAsync");

        Step(page).Should().Be("Totp");
        Get<string>(page, "_error").Should().Contain("заново");
        Invoke(page, "Restart");
        AssertSecretsCleared(page);
        Step(page).Should().Be("Credentials");
        FillCredentials(page);
        await InvokeAsync(page, "BeginAsync");
        Get<Guid>(page, "_challengeId").Should().Be(replacement.ChallengeId);
        SetForm(page, "_confirmation", "Code", "654321");
        await InvokeAsync(page, "ConfirmAsync");
        Step(page).Should().Be("Done");
        AssertSecretsCleared(page);
    }

    [Fact]
    public async Task Begin_DisposedDuringRequest_ShouldNotRestoreSecrets()
    {
        var response = new TaskCompletionSource<SetupChallengeDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new StubAuthenticationGateway { Begin = (_, _, _) => response.Task };
        using var page = CreatePage(gateway);
        FillCredentials(page);
        var pending = InvokeAsync(page, "BeginAsync");

        page.Dispose();
        response.SetResult(Challenge(DateTimeOffset.UtcNow.AddMinutes(5)));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        AssertSecretsCleared(page);
        Step(page).Should().Be("Credentials");
    }

    [Fact]
    public async Task Confirm_DisposedDuringRequest_ShouldIgnoreLateError()
    {
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new StubAuthenticationGateway
        {
            Begin = (_, _, _) => Task.FromResult(Challenge(DateTimeOffset.UtcNow.AddMinutes(5))),
            Confirm = (_, _, _) => response.Task
        };
        using var page = CreatePage(gateway);
        FillCredentials(page);
        await InvokeAsync(page, "BeginAsync");
        SetForm(page, "_confirmation", "Code", "123456");
        var pending = InvokeAsync(page, "ConfirmAsync");

        page.Dispose();
        response.SetException(new ApiException("Токен настройки отклонён.", HttpStatusCode.Forbidden));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Get<string?>(page, "_error").Should().BeNull();
        AssertSecretsCleared(page);
    }

    [Fact]
    public async Task Initialize_DisposedPage_ShouldSkipRequest()
    {
        var gateway = new StubAuthenticationGateway();
        using var page = CreatePage(gateway);
        page.Dispose();

        await InvokeAsync(page, "OnInitializedAsync");

        gateway.StatusRequests.Should().Be(0);
    }

    [Fact]
    public async Task Render_AdministratorSetupRoute_ShouldUseSharedWizard()
    {
        var gateway = new StubAuthenticationGateway();

        var html = await RenderAsync<AdministratorSetup>(gateway);

        typeof(AdministratorSetup).GetCustomAttributes<RouteAttribute>().Select(route => route.Template)
            .Should().Contain("/admin/setup");
        typeof(Setup).GetCustomAttributes<RouteAttribute>().Select(route => route.Template)
            .Should().Contain("/setup");
        gateway.StatusRequests.Should().Be(1);
        html.Should().Contain("Одноразовый токен настройки").And.Contain("type=\"password\"");
    }

    [Fact]
    public async Task Render_CompletedSetup_ShouldExplainNextTotpCode()
    {
        var html = await RenderAsync<CompletedSetup>(new StubAuthenticationGateway());

        html.Should().Contain("дождитесь следующего шестизначного кода")
            .And.Contain("код подтверждения уже использован");
    }

    [Fact]
    public async Task Render_ChallengeStep_ShouldShowExpiryAndRestart()
    {
        var html = await RenderAsync<ActiveSetup>(new StubAuthenticationGateway());

        html.Should().Contain("Попытка действует до").And.Contain("Начать заново")
            .And.Contain("Если срок истёк или попытки исчерпаны");
    }

    private static Setup CreatePage(IAuthenticationGateway gateway)
    {
        var page = new Setup();
        typeof(Setup).GetProperty("Authentication", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, gateway);
        return page;
    }

    private static SetupChallengeDto Challenge(DateTimeOffset expiresAt) =>
        new(Guid.NewGuid(), "JBSWY3DPEHPK3PXP", "otpauth://totp/Synthetic", expiresAt);

    private static void FillCredentials(Setup page)
    {
        SetForm(page, "_credentials", "SetupToken", "synthetic-owner-token");
        SetForm(page, "_credentials", "Password", "Aa1234567890");
        SetForm(page, "_credentials", "ConfirmPassword", "Aa1234567890");
    }

    private static void AssertSecretsCleared(Setup page)
    {
        Get<Guid>(page, "_challengeId").Should().BeEmpty();
        Get<DateTimeOffset?>(page, "_challengeExpiresAt").Should().BeNull();
        Get<string>(page, "_secret").Should().BeEmpty();
        Get<string>(page, "_authenticatorUri").Should().BeEmpty();
        Get<string>(page, "_setupToken").Should().BeEmpty();
        GetForm(page, "_credentials", "SetupToken").Should().BeEmpty();
        GetForm(page, "_credentials", "Password").Should().BeEmpty();
        GetForm(page, "_credentials", "ConfirmPassword").Should().BeEmpty();
        GetForm(page, "_confirmation", "Code").Should().BeEmpty();
    }

    private static async Task<string> RenderAsync<TComponent>(IAuthenticationGateway gateway)
        where TComponent : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(gateway);
        services.AddSingleton<IJSRuntime>(new Mock<IJSRuntime>().Object);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TComponent>();
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    private static T Get<T>(Setup page, string field) => (T)typeof(Setup)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static void Set(Setup page, string field, object? value) => typeof(Setup)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static void SetForm(Setup page, string field, string property, string value) =>
        Get<object>(page, field).GetType().GetProperty(property)!.SetValue(Get<object>(page, field), value);
    private static string GetForm(Setup page, string field, string property) =>
        (string)Get<object>(page, field).GetType().GetProperty(property)!.GetValue(Get<object>(page, field))!;
    private static string Step(Setup page) => Get<object>(page, "_step").ToString()!;
    private static void SetStep(Setup page, string value) =>
        Set(page, "_step", Enum.Parse(typeof(Setup).GetField("_step", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType, value));
    private static object? Invoke(Setup page, string method) => typeof(Setup)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
    private static Task InvokeAsync(Setup page, string method) => (Task)Invoke(page, method)!;

    private sealed class CompletedSetup : Setup
    {
        public CompletedSetup() { Set(this, "_checking", false); SetStep(this, "Done"); }
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class ActiveSetup : Setup
    {
        public ActiveSetup()
        {
            Set(this, "_checking", false);
            SetStep(this, "Totp");
            Set(this, "_challengeExpiresAt", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class StubAuthenticationGateway : IAuthenticationGateway
    {
        public int StatusRequests { get; private set; }
        public Func<BeginSetupRequest, string, CancellationToken, Task<SetupChallengeDto>> Begin { get; init; } =
            (_, _, _) => throw new NotSupportedException();
        public Func<ConfirmSetupRequest, string, CancellationToken, Task> Confirm { get; init; } =
            (_, _, _) => Task.CompletedTask;

        public Task<SetupStatusDto> GetSetupStatusAsync(CancellationToken cancellationToken)
        {
            StatusRequests++;
            return Task.FromResult(new SetupStatusDto(true));
        }
        public Task<SetupChallengeDto> BeginSetupAsync(BeginSetupRequest request, string setupToken,
            CancellationToken cancellationToken) => Begin(request, setupToken, cancellationToken);
        public Task ConfirmSetupAsync(ConfirmSetupRequest request, string setupToken,
            CancellationToken cancellationToken) => Confirm(request, setupToken, cancellationToken);
        public Task<AuthenticationSessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<AuthenticationSessionDto?> GetSessionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
