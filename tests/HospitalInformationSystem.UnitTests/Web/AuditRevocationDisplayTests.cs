using System.Net;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.Components.Audit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class AuditRevocationDisplayTests
{
    [Fact]
    public void ServiceLogin_ShouldHaveItsOwnLabelAndStringContract()
    {
        global::HospitalInformationSystem.Web.AuditLabels.EventType(AuditEventType.ServiceLogin)
            .Should().Be("Вход сервисного аккаунта");
        JsonSerializer.Serialize(AuditEventType.ServiceLogin).Should().Be("\"ServiceLogin\"");
        JsonSerializer.Deserialize<AuditEventType>("\"ServiceLogin\"").Should().Be(AuditEventType.ServiceLogin);
        global::HospitalInformationSystem.Web.AuditLabels.EventType(AuditEventType.UserLogin)
            .Should().Be("Вход сотрудника");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Render_RefreshTokensRevoked_ShouldShowExactCount(int count)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["revokedRefreshTokenCount"] = JsonSerializer.SerializeToElement(count)
        };

        var html = await RenderAsync(metadata);

        html.Should().Contain($"Отозвано refresh-токенов: {count}.");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"revokedRefreshTokenCount\":null}")]
    [InlineData("{\"revokedRefreshTokenCount\":\"2\"}")]
    [InlineData("{\"revokedRefreshTokenCount\":true}")]
    [InlineData("{\"revokedRefreshTokenCount\":{}}")]
    [InlineData("{\"revokedRefreshTokenCount\":[]}")]
    [InlineData("{\"revokedRefreshTokenCount\":-1}")]
    [InlineData("{\"revokedRefreshTokenCount\":1.5}")]
    [InlineData("{\"revokedRefreshTokenCount\":2147483648}")]
    public async Task Render_InvalidRevocationMetadata_ShouldOmitCountWithoutFailing(string json)
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var html = await RenderAsync(metadata);

        html.Should().Contain("Сброшен пароль учётной записи.")
            .And.NotContain("Отозвано refresh-токенов");
    }

    [Fact]
    public async Task Render_ServiceActor_ShouldShowServiceNameAndIdWithoutHumanLabel()
    {
        var serviceId = Guid.NewGuid();
        var actor = new AuditActorDto(AuditActorKind.ServiceAccount, null, "lis.external", "request", null, serviceId);

        var html = await RenderAsync(new Dictionary<string, JsonElement>(), actor);

        html.Should().Contain("Сервисный аккаунт").And.Contain("lis.external")
            .And.Contain($"ID сервиса: {serviceId}").And.NotContain("Пользователь");
    }

    private static async Task<string> RenderAsync(IReadOnlyDictionary<string, JsonElement>? metadata,
        AuditActorDto? actor = null)
    {
        var item = new AuditEventDto(Guid.NewGuid(), DateTimeOffset.UtcNow, AuditEventType.AccountChanged,
            actor ?? new AuditActorDto(AuditActorKind.User, Guid.NewGuid(), "Администратор", null, null),
            "Сброшен пароль учётной записи.", metadata!);
        var services = new ServiceCollection().AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AuditTable>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(AuditTable.Items)] = new[] { item } }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
