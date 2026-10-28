using System.Net;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Web.Components.Audit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class AuditRevocationDisplayTests
{
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

    private static async Task<string> RenderAsync(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        var item = new AuditEventDto(Guid.NewGuid(), DateTimeOffset.UtcNow, AuditEventType.AccountChanged,
            new AuditActorDto(AuditActorKind.User, Guid.NewGuid(), "Администратор", null, null),
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
