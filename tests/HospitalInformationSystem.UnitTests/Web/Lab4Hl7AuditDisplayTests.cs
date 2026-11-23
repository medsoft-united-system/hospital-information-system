using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Components.Audit;
using HospitalInformationSystem.Web.Development;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class Lab4Hl7AuditDisplayTests
{
    private static readonly Guid ServiceId = Guid.Parse("19000000-0000-4000-8000-000000000001");
    private static readonly Guid PatientId = Guid.Parse("29000000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset EventTime = new(2026, 11, 17, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void EventLabel_ShouldIdentifyHl7ImportFilter()
    {
        global::HospitalInformationSystem.Web.AuditLabels.EventType(AuditEventType.Hl7PatientImported)
            .Should().Be("Импорт (HL7)");
    }

    [Theory]
    [InlineData("success", "Успех")]
    [InlineData("duplicate", "Дубликат")]
    [InlineData("validation-failed", "Ошибка")]
    [InlineData("auth-rejected", "Ошибка")]
    [InlineData("protocol-rejected", "Ошибка")]
    [InlineData("internal-error", "Ошибка")]
    [InlineData("unsupported-result", "Неизвестно")]
    public async Task Render_ImportOutcome_ShouldShowReadableResult(string outcome, string expected)
    {
        var metadata = Metadata();
        metadata["outcome"] = JsonSerializer.SerializeToElement(outcome);

        var html = WebUtility.HtmlDecode(await RenderAsync(Item(metadata)));

        html.Should().Contain(expected).And.Contain("Импорт и сертификат")
            .And.Contain("Клиентский сертификат");
    }

    [Fact]
    public async Task Render_Success_ShouldShowIdsServiceAndActualCertificateFields()
    {
        var html = WebUtility.HtmlDecode(await RenderAsync(Item(Metadata())));

        html.Should().Contain("Импорт (HL7)").And.Contain("hl7.import.actual")
            .And.Contain(ServiceId.ToString()).And.Contain("message-001").And.Contain("LAB4_XML")
            .And.Contain("000042").And.Contain(PatientId.ToString())
            .And.Contain("Принято диагнозов").And.Contain(new string('a', 64))
            .And.Contain("CN=Actual desktop, OU=Lab").And.Contain("CN=Actual issuer")
            .And.Contain("01ABCDEF").And.Contain("16.11.2026 12:34:56 UTC")
            .And.Contain("17.11.2027 12:34:56 UTC");
        html.Should().Contain(EventTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))
            .And.Contain("<details").And.Contain("<summary");
    }

    [Fact]
    public async Task Render_ImportDisclosure_ShouldHaveItsOwnFullWidthEventRow()
    {
        var item = Item(Metadata());

        var html = await RenderAsync(item);

        html.Should().Contain($"data-event-id=\"{item.EventId}\"")
            .And.Contain($"data-details-for=\"{item.EventId}\"")
            .And.Contain("hl7-audit-details-row")
            .And.Contain("colspan=\"5\"")
            .And.Contain("<details");
    }

    [Fact]
    public async Task Render_Duplicate_ShouldIdentifyExistingPatientAndZeroAcceptedDiagnoses()
    {
        var metadata = Metadata();
        metadata["outcome"] = JsonSerializer.SerializeToElement(Hl7ImportAuditOutcomes.Duplicate);
        metadata["diagnosisCount"] = JsonSerializer.SerializeToElement(0);

        var projection = Hl7ImportAuditDisplayFactory.Create(Item(metadata))!;
        var html = WebUtility.HtmlDecode(await RenderAsync(Item(metadata)));

        projection.PatientId.Should().Be(PatientId);
        projection.AcceptedDiagnoses.Should().Be(0);
        html.Should().Contain("Дубликат").And.Contain("Пациент HIS").And.Contain(PatientId.ToString())
            .And.NotContain("Созданный пациент");
    }

    [Fact]
    public async Task Render_AnonymousRefusal_ShouldOmitUnverifiedServiceClaims()
    {
        var metadata = Metadata();
        metadata["outcome"] = JsonSerializer.SerializeToElement(Hl7ImportAuditOutcomes.AuthenticationRejected);
        metadata["serviceAccountId"] = JsonSerializer.SerializeToElement(ServiceId);
        metadata["serviceAccountName"] = JsonSerializer.SerializeToElement("spoofed-service");
        var actor = new AuditActorDto(AuditActorKind.Anonymous, null, "Неаутентифицированный запрос",
            "message-001", "hl7-import");
        var item = Item(metadata) with { Actor = actor };
        var projection = Hl7ImportAuditDisplayFactory.Create(item)!;

        var html = WebUtility.HtmlDecode(await RenderAsync(item));

        projection.ServiceName.Should().BeNull();
        projection.ServiceId.Should().BeNull();
        html.Should().Contain("Аноним").And.Contain("Не установлен").And.Contain("Ошибка")
            .And.Contain("CN=Actual desktop, OU=Lab").And.NotContain("spoofed-service")
            .And.NotContain($"ID сервиса: {ServiceId}");
    }

    [Fact]
    public void Projection_ShouldPreferRequestIdAndFallBackToLegacyCorrelation()
    {
        var metadata = Metadata();
        metadata["correlationId"] = JsonSerializer.SerializeToElement("legacy-message");
        var item = Item(metadata);

        Hl7ImportAuditDisplayFactory.Create(item)!.MessageId.Should().Be("message-001");
        Hl7ImportAuditDisplayFactory.Create(item with { Actor = item.Actor with { RequestId = null } })!
            .MessageId.Should().Be("legacy-message");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"outcome\":{},\"diagnosisCount\":-1}")]
    [InlineData("{\"outcome\":true,\"targetId\":\"bad-id\"}")]
    [InlineData("{\"clientCertificateSha256\":\"bad-hash\",\"clientCertificateSubject\":[]}")]
    [InlineData("{\"clientCertificateNotBeforeUtc\":\"bad-time\",\"clientCertificateNotAfterUtc\":42}")]
    [InlineData("{\"diagnosisCount\":1.5,\"externalPatientId\":{}}")]
    [InlineData("{\"diagnosisCount\":2147483648,\"sourceSystem\":false}")]
    [InlineData("{\"diagnosisCount\":101,\"clientCertificateSerialNumber\":[]}")]
    public async Task Render_MissingOrMalformedMetadata_ShouldShowFallbacksWithoutFailure(string json)
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var html = WebUtility.HtmlDecode(await RenderAsync(Item(metadata)));

        html.Should().Contain("Импорт (HL7)").And.Contain("Неизвестно")
            .And.Contain("Не указан").And.Contain("Импорт и сертификат");
    }

    [Fact]
    public void Projection_ReversedValidityAndOversizedText_ShouldOmitInvalidValues()
    {
        var metadata = Metadata();
        metadata["clientCertificateNotBeforeUtc"] = JsonSerializer.SerializeToElement(EventTime.AddYears(2));
        metadata["clientCertificateNotAfterUtc"] = JsonSerializer.SerializeToElement(EventTime);
        metadata["clientCertificateSubject"] = JsonSerializer.SerializeToElement(new string('x', 2049));
        metadata["clientCertificateSha256"] = JsonSerializer.SerializeToElement(new string('z', 64));
        metadata["diagnosisCount"] = JsonSerializer.SerializeToElement(101);

        var projection = Hl7ImportAuditDisplayFactory.Create(Item(metadata))!;

        projection.CertificateNotBefore.Should().BeNull();
        projection.CertificateNotAfter.Should().BeNull();
        projection.CertificateSubject.Should().BeNull();
        projection.CertificateSha256.Should().BeNull();
        projection.AcceptedDiagnoses.Should().BeNull();
    }

    [Fact]
    public async Task Render_Metadata_ShouldEscapeHtmlAndNeverExposeUnlistedSecrets()
    {
        const string payload = "<img src=x onerror=alert(1)>";
        var metadata = Metadata();
        metadata["clientCertificateSubject"] = JsonSerializer.SerializeToElement(payload);
        metadata["externalPatientId"] = JsonSerializer.SerializeToElement(payload);
        metadata["accessToken"] = JsonSerializer.SerializeToElement("hidden-access-token");
        metadata["apiKey"] = JsonSerializer.SerializeToElement("hidden-api-key");
        metadata["uac"] = JsonSerializer.SerializeToElement("UAC|hidden-credential");
        metadata["rawXml"] = JsonSerializer.SerializeToElement("<Patients>hidden-xml</Patients>");
        var item = Item(metadata) with { Actor = Item(metadata).Actor with { DisplayName = payload } };

        var html = await RenderAsync(item);

        html.Should().Contain("&lt;img").And.NotContain("<img")
            .And.NotContain("hidden-access-token").And.NotContain("hidden-api-key")
            .And.NotContain("UAC|hidden-credential").And.NotContain("hidden-xml");
    }

    [Fact]
    public async Task Render_OtherEvent_ShouldKeepRefreshSummary()
    {
        var item = Item(new Dictionary<string, JsonElement>
        {
            ["revokedServiceSessionCount"] = JsonSerializer.SerializeToElement(3),
            ["clientCertificateSubject"] = JsonSerializer.SerializeToElement("unused-cert-field")
        }) with
        { EventType = AuditEventType.AccountChanged };

        var html = WebUtility.HtmlDecode(await RenderAsync(item));

        html.Should().Contain("Отозвано сервисных сессий: 3.")
            .And.NotContain("Импорт и сертификат").And.NotContain("unused-cert-field");
    }

    [Fact]
    public async Task UiMock_ImportFilter_ShouldReturnServiceEventsWithDisplayableDetails()
    {
        var state = new UiMockState(new SessionState()) { EnableLiveAudit = false };
        state.SwitchPersona(UiMockPersona.Administrator);
        using var http = new HttpClient(new UiMockHttpMessageHandler(state))
        {
            BaseAddress = new Uri("https://ui-mock.invalid/")
        };

        var response = await http.GetFromJsonAsync<PagedResult<AuditEventDto>>(
            "api/audit?eventType=Hl7PatientImported&page=1&pageSize=50");

        response!.Items.Should().HaveCount(3).And.OnlyContain(item =>
            item.Actor.Kind == AuditActorKind.ServiceAccount && item.Actor.ServiceAccountId.HasValue);
        var details = response.Items.Select(item => Hl7ImportAuditDisplayFactory.Create(item)!).ToArray();
        details.Select(detail => detail.ResultLabel).Should().BeEquivalentTo("Успех", "Дубликат", "Ошибка");
        details.Should().OnlyContain(detail => detail.CertificateSha256 != null &&
            detail.ExternalPatientId == "000042");
    }

    private static AuditEventDto Item(IReadOnlyDictionary<string, JsonElement>? metadata) => new(
        Guid.NewGuid(), EventTime, AuditEventType.Hl7PatientImported,
        new AuditActorDto(AuditActorKind.ServiceAccount, null, "hl7.import.actual", "message-001",
            "hl7-import", ServiceId), "Пациент импортирован через HL7.", metadata!);

    private static Dictionary<string, JsonElement> Metadata() => new()
    {
        ["outcome"] = JsonSerializer.SerializeToElement(Hl7ImportAuditOutcomes.Success),
        ["correlationId"] = JsonSerializer.SerializeToElement("message-001"),
        ["sourceSystem"] = JsonSerializer.SerializeToElement(Hl7ImportProtocol.SourceSystem),
        ["externalPatientId"] = JsonSerializer.SerializeToElement("000042"),
        ["targetId"] = JsonSerializer.SerializeToElement(PatientId),
        ["diagnosisCount"] = JsonSerializer.SerializeToElement(2),
        ["clientCertificateSha256"] = JsonSerializer.SerializeToElement(new string('a', 64)),
        ["clientCertificateSubject"] = JsonSerializer.SerializeToElement("CN=Actual desktop, OU=Lab"),
        ["clientCertificateIssuer"] = JsonSerializer.SerializeToElement("CN=Actual issuer"),
        ["clientCertificateSerialNumber"] = JsonSerializer.SerializeToElement("01ABCDEF"),
        ["clientCertificateNotBeforeUtc"] = JsonSerializer.SerializeToElement(EventTime.AddDays(-1)),
        ["clientCertificateNotAfterUtc"] = JsonSerializer.SerializeToElement(EventTime.AddYears(1))
    };

    private static async Task<string> RenderAsync(AuditEventDto item)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AuditTable>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(AuditTable.Items)] = new[] { item } }));
            return component.ToHtmlString();
        });
    }
}
