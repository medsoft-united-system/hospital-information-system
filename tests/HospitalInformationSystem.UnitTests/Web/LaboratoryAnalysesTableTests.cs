using System.Net;
using FluentAssertions;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Web.Components.MedicalRecords;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryAnalysesTableTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task CompletedResult_ShouldShowSnapshotDoctorAndBothTimesWithSeconds()
    {
        var request = Request();
        request = request with
        {
            Status = LaboratoryAnalysisRequestStatus.Completed,
            RequestedByNameSnapshot = "Врач в момент назначения",
            Result = new LaboratoryAnalysisResultDto(Guid.NewGuid(), request.Id, request.PatientId, "0",
                At.AddMinutes(1), At.AddMinutes(2), Guid.NewGuid())
        };

        var html = WebUtility.HtmlDecode(await RenderAsync([request]));

        html.Should().Contain("Врач в момент назначения").And.Contain("Глюкоза").And.Contain("LOINC 2345-7")
            .And.Contain("Выполнен").And.Contain("без единиц измерения")
            .And.NotContain("Учебный режим:")
            .And.Contain(LaboratoryAnalysisPresentation.FormatDate(At))
            .And.Contain(LaboratoryAnalysisPresentation.FormatDate(At.AddMinutes(1)))
            .And.Contain(LaboratoryAnalysisPresentation.FormatDate(At.AddMinutes(2)))
            .And.Contain(">0</td>");
    }

    [Fact]
    public async Task HistoricalTable_ShouldUseOnlyPassedSnapshotAndNeverInventAResult()
    {
        var request = Request();
        var live = request with { Result = new LaboratoryAnalysisResultDto(Guid.NewGuid(), request.Id,
            request.PatientId, "UNIQUE_FUTURE_RESULT", At.AddDays(1), At.AddDays(1), Guid.NewGuid()) };

        var html = WebUtility.HtmlDecode(await RenderAsync([request], historical: true));

        html.Should().Contain("на выбранный момент времени").And.Contain("Назначен").And.Contain("—")
            .And.NotContain(live.Result!.Value).And.NotContain("Выполнен")
            .And.NotContain("Назначить анализы");
    }

    [Fact]
    public async Task Table_ShouldEscapeMedicalTextAndShowEmptyHistoricalState()
    {
        var request = Request() with
        {
            DisplayName = "<script>alert(1)</script>", Comment = "<b>Комментарий</b>",
            RequestedByNameSnapshot = "<b>Имя врача</b>"
        };
        var html = await RenderAsync([request]);

        html.Should().NotContain("<script>").And.NotContain("<b>Комментарий</b>")
            .And.NotContain("<b>Имя врача</b>").And.Contain("&lt;script&gt;").And.Contain("&lt;b&gt;");
        WebUtility.HtmlDecode(await RenderAsync([], historical: true)).Should()
            .Contain("На выбранный момент анализы не были назначены.");
    }

    [Fact]
    public async Task MissingRequesterSnapshot_ShouldShowIdentifierWithoutRequestingHistory()
    {
        var request = Request();

        var html = WebUtility.HtmlDecode(await RenderAsync([request]));

        html.Should().Contain("Медработник · " + request.RequestedByMedicalWorkerId.ToString("N")[..8]);
    }

    private static LaboratoryAnalysisRequestDto Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "2345-7", "Глюкоза", null, At, LaboratoryAnalysisRequestStatus.Active, null);

    private static async Task<string> RenderAsync(IReadOnlyCollection<LaboratoryAnalysisRequestDto> requests,
        bool historical = false)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<LaboratoryAnalysesTable>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(LaboratoryAnalysesTable.Requests)] = requests,
                    [nameof(LaboratoryAnalysesTable.Historical)] = historical
                }))).ToHtmlString());
    }
}
