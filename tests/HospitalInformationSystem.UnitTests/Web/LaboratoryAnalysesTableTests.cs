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
            Result = new LaboratoryAnalysisResultDto(Guid.NewGuid(), request.Id, request.PatientId, "0",
                At.AddMinutes(1), At.AddMinutes(2), Guid.NewGuid())
        };

        var html = WebUtility.HtmlDecode(await RenderAsync([request], new Dictionary<Guid, string>
        { [request.Id] = "Врач в момент назначения" }));

        html.Should().Contain("Врач в момент назначения").And.Contain("Глюкоза").And.Contain("LOINC 2345-7")
            .And.Contain("Выполнен").And.Contain("Учебный режим: единицы измерения не используются")
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
        var request = Request() with { DisplayName = "<script>alert(1)</script>", Comment = "<b>Комментарий</b>" };
        var html = await RenderAsync([request]);

        html.Should().NotContain("<script>").And.NotContain("<b>Комментарий</b>")
            .And.Contain("&lt;script&gt;");
        WebUtility.HtmlDecode(await RenderAsync([], historical: true)).Should()
            .Contain("На выбранный момент анализы не были назначены.");
    }

    [Fact]
    public void RequesterNames_ShouldUseHumanRequestSnapshotsAndIgnoreResultActors()
    {
        var request = Request();
        var human = new MedicalRecordHistoryEventDto(1, At, request.RequestedByMedicalWorkerId,
            "Прежнее имя врача", "Врач", "LaboratoryAnalysisRequested", request.Id, "{}");
        var service = human with { Version = 2, PerformedByName = "lab-service", EventType = "LaboratoryAnalysisResultRecorded",
            AuthorKind = MedicalRecordAuthorKind.ServiceAccount, PerformedByMedicalWorkerId = null,
            PerformedByServiceAccountId = Guid.NewGuid() };

        LaboratoryAnalysisPresentation.RequesterNames([human, service]).Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<Guid, string>(request.Id, "Прежнее имя врача"));
    }

    private static LaboratoryAnalysisRequestDto Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "2345-7", "Глюкоза", null, At, LaboratoryAnalysisRequestStatus.Active, null);

    private static async Task<string> RenderAsync(IReadOnlyCollection<LaboratoryAnalysisRequestDto> requests,
        IReadOnlyDictionary<Guid, string>? names = null, bool historical = false)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<LaboratoryAnalysesTable>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(LaboratoryAnalysesTable.Requests)] = requests,
                    [nameof(LaboratoryAnalysesTable.RequesterNames)] = names ?? new Dictionary<Guid, string>(),
                    [nameof(LaboratoryAnalysesTable.Historical)] = historical
                }))).ToHtmlString());
    }
}
