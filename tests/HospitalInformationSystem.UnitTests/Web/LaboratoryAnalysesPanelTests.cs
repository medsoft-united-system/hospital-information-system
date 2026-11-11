using System.Net;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Components.MedicalRecords;
using HospitalInformationSystem.Web.Gateways;
using Microsoft.AspNetCore.Components;
using Moq;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryAnalysesPanelTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task Reader_ShouldNotLoadCatalogOrSendAnOrderEvenWhenHandlerInvokedDirectly()
    {
        var catalog = Catalog();
        var records = Records();
        using var panel = await PanelAsync(Record(), records, catalog, canAssign: false);

        await InvokeAsync(panel, "OpenFormAsync");
        await InvokeAsync(panel, "SubmitAsync");

        catalog.Verify(x => x.ListAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        records.Verify(x => x.CreateLaboratoryAnalysisRequestsAsync(It.IsAny<Guid>(),
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        records.Verify(x => x.GetHistoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Get<bool>(panel, "_formOpen").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedSession_ShouldCloseFormKeepGlobalErrorAndReleaseParentBusy(HttpStatusCode status)
    {
        var record = Record();
        var definition = Definition();
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
                It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Запрос отклонён сервером.", status));
        using var panel = await PanelAsync(record, records, Catalog(definition));
        var busyStates = new List<bool>();
        var confirmedCallbacks = 0;
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.BusyChanged),
            EventCallback.Factory.Create<bool>(new object(), busyStates.Add));
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.RequestsCreated),
            EventCallback.Factory.Create<IReadOnlyList<LaboratoryAnalysisRequestDto>>(
                new object(), _ => confirmedCallbacks++));
        await InvokeAsync(panel, "OpenFormAsync");
        Toggle(panel, definition.Id);

        await InvokeAsync(panel, "SubmitAsync");

        Get<bool>(panel, "_formOpen").Should().BeFalse();
        Get<string>(panel, "_error").Should().Contain("Недостаточно прав");
        Get<bool>(panel, "_submitting").Should().BeFalse();
        busyStates.Should().Equal(true, false);
        Visible(panel).Should().BeEmpty();
        confirmedCallbacks.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmedBatch_ShouldSubmitSelectedActiveIdsAndClearFormBeforeRefreshCallback()
    {
        var record = Record();
        var first = Definition();
        var second = Definition("718-7");
        var inactive = Definition("5196-1") with { IsActive = false };
        var catalog = Catalog(first, second, inactive);
        var records = Records();
        IReadOnlyList<LaboratoryAnalysisRequestDto> created = [Request(record, first), Request(record, second)];
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
                It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateLaboratoryAnalysisRequestsRequest, CancellationToken>((_, request, _) =>
            {
                request.LaboratoryTestDefinitionIds.Should().BeEquivalentTo([first.Id, second.Id]);
                request.Comment.Should().Be("Натощак");
                request.ExpectedVersions.Should().BeEquivalentTo(new Dictionary<Guid, uint>
                    { [first.Id] = first.Version, [second.Id] = second.Version });
            }).ReturnsAsync(created);
        using var panel = await PanelAsync(record, records, catalog);
        await InvokeAsync(panel, "OpenFormAsync");
        Toggle(panel, first.Id); Toggle(panel, second.Id); Toggle(panel, inactive.Id);
        Set(panel, "_comment", " Натощак ");
        var refreshed = 0;
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.RequestsCreated), EventCallback.Factory.Create<IReadOnlyList<LaboratoryAnalysisRequestDto>>(
            new object(), _ =>
            {
                Get<bool>(panel, "_formOpen").Should().BeFalse();
                Get<HashSet<Guid>>(panel, "_selected").Should().BeEmpty();
                refreshed++;
            }));

        await InvokeAsync(panel, "SubmitAsync");

        Visible(panel).Should().BeEquivalentTo(created);
        refreshed.Should().Be(1);
        Get<string>(panel, "_notice").Should().Contain("Создано отдельных заказов: 2");
        catalog.Verify(x => x.ListAsync(It.IsAny<CancellationToken>(), true), Times.Once);
        records.Verify(x => x.GetHistoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmedPost_FailedParentRefresh_ShouldRemainVisibleWithoutResubmitting()
    {
        var record = Record();
        var definition = Definition();
        var created = Request(record, definition);
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([created]);
        using var panel = await PanelAsync(record, records, Catalog(definition));
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.RequestsCreated), EventCallback.Factory.Create<IReadOnlyList<LaboratoryAnalysisRequestDto>>(
            new object(), (Func<IReadOnlyList<LaboratoryAnalysisRequestDto>, Task>)(_ => throw new HttpRequestException())));
        await InvokeAsync(panel, "OpenFormAsync"); Toggle(panel, definition.Id);

        await InvokeAsync(panel, "SubmitAsync");
        await InvokeAsync(panel, "SubmitAsync");

        Visible(panel).Should().Equal(created);
        Get<bool>(panel, "_formOpen").Should().BeFalse();
        Get<string>(panel, "_notice").Should().Contain("сохранено").And.Contain("не удалось");
        records.Verify(x => x.CreateLaboratoryAnalysisRequestsAsync(It.IsAny<Guid>(),
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PendingSubmission_ShouldBlockDoubleClickAndChangingSelection()
    {
        var record = Record();
        var definition = Definition();
        var other = Definition("718-7");
        var completion = new TaskCompletionSource<IReadOnlyList<LaboratoryAnalysisRequestDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>())).Returns(completion.Task);
        using var panel = await PanelAsync(record, records, Catalog(definition, other));
        await InvokeAsync(panel, "OpenFormAsync"); Toggle(panel, definition.Id);

        var first = InvokeAsync(panel, "SubmitAsync");
        await InvokeAsync(panel, "SubmitAsync");
        Toggle(panel, other.Id);
        Invoke(panel, "CloseForm");
        Get<HashSet<Guid>>(panel, "_selected").Should().Equal(definition.Id);
        Get<bool>(panel, "_formOpen").Should().BeTrue();
        completion.SetResult([Request(record, definition)]);
        await first;

        records.Verify(x => x.CreateLaboratoryAnalysisRequestsAsync(It.IsAny<Guid>(),
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LatePost_FromPreviousRecord_ShouldNeverPopulateNewPatientOrNotifyParent()
    {
        var record = Record();
        var definition = Definition();
        var completion = new TaskCompletionSource<IReadOnlyList<LaboratoryAnalysisRequestDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
                It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateLaboratoryAnalysisRequestsRequest, CancellationToken>((_, _, token) => captured = token)
            .Returns(completion.Task);
        using var panel = await PanelAsync(record, records, Catalog(definition));
        var callbacks = 0;
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.RequestsCreated), EventCallback.Factory.Create<IReadOnlyList<LaboratoryAnalysisRequestDto>>(
            new object(), _ => callbacks++));
        await InvokeAsync(panel, "OpenFormAsync"); Toggle(panel, definition.Id);
        var send = InvokeAsync(panel, "SubmitAsync");

        SetProperty(panel, nameof(LaboratoryAnalysesPanel.Record), Record());
        await InvokeAsync(panel, "OnParametersSetAsync");
        completion.SetResult([Request(record, definition)]);
        await send;

        captured.IsCancellationRequested.Should().BeTrue();
        Visible(panel).Should().BeEmpty();
        callbacks.Should().Be(0);
    }

    [Fact]
    public async Task ServerRefresh_WithCompletedResult_ShouldTakePrecedenceOverLocalCreatedOrder()
    {
        var record = Record();
        var definition = Definition();
        var created = Request(record, definition);
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([created]);
        using var panel = await PanelAsync(record, records, Catalog(definition));
        await InvokeAsync(panel, "OpenFormAsync"); Toggle(panel, definition.Id);
        await InvokeAsync(panel, "SubmitAsync");
        var completed = created with
        {
            Status = LaboratoryAnalysisRequestStatus.Completed,
            Result = new LaboratoryAnalysisResultDto(Guid.NewGuid(), created.Id, record.PatientId, "142", At, At, Guid.NewGuid())
        };

        SetProperty(panel, nameof(LaboratoryAnalysesPanel.Record), record with { Version = 3, LaboratoryAnalysisRequests = [completed] });
        await InvokeAsync(panel, "OnParametersSetAsync");

        Visible(panel).Should().Equal(completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidComment_ShouldNotSend_UnicodeBoundaryShouldRemainAccepted(bool nul)
    {
        var record = Record();
        var definition = Definition();
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([Request(record, definition)]);
        using var panel = await PanelAsync(record, records, Catalog(definition));
        await InvokeAsync(panel, "OpenFormAsync"); Toggle(panel, definition.Id);
        var boundary = string.Concat(Enumerable.Repeat("🧪", 2000));
        Set(panel, "_comment", nul ? "Текст\0комментария" : boundary + "Я");

        await InvokeAsync(panel, "SubmitAsync");

        records.Verify(x => x.CreateLaboratoryAnalysisRequestsAsync(It.IsAny<Guid>(),
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Set(panel, "_comment", boundary);
        await InvokeAsync(panel, "SubmitAsync");
        records.Verify(x => x.CreateLaboratoryAnalysisRequestsAsync(It.IsAny<Guid>(),
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForeignPatientResponse_ShouldNotDisplayOrdersOrCloseFormAsSuccess()
    {
        var record = Record();
        var definition = Definition();
        var records = Records();
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
            It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Request(record, definition) with { PatientId = Guid.NewGuid() }]);
        using var panel = await PanelAsync(record, records, Catalog(definition));
        await InvokeAsync(panel, "OpenFormAsync"); Toggle(panel, definition.Id);

        await InvokeAsync(panel, "SubmitAsync");

        Visible(panel).Should().BeEmpty();
        Get<bool>(panel, "_formOpen").Should().BeTrue();
        Get<string>(panel, "_formError").Should().Contain("другой карте");
    }

    [Fact]
    public async Task Dispose_ShouldCancelPendingCatalogAndIgnoreItsLateResponse()
    {
        var catalog = Catalog();
        var pending = new TaskCompletionSource<IReadOnlyList<LaboratoryTestDefinitionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        catalog.Setup(x => x.ListAsync(It.IsAny<CancellationToken>(), true))
            .Callback<CancellationToken, bool>((value, _) => token = value).Returns(pending.Task);
        using var panel = await PanelAsync(Record(), Records(), catalog);
        var load = InvokeAsync(panel, "OpenFormAsync");

        panel.Dispose();
        pending.SetResult([Definition()]);
        await load;

        token.IsCancellationRequested.Should().BeTrue();
        Get<IReadOnlyList<LaboratoryTestDefinitionDto>?>(panel, "_catalog").Should().BeNull();
    }

    [Fact]
    public async Task CatalogConflict_ShouldRequireExplicitReloadAndReselectionWithoutSilentlyReplacingVersions()
    {
        var record = Record();
        var original = Definition();
        var latest = original with { Version = original.Version + 1, DisplayName = "Уточнённое исследование" };
        var catalog = Catalog(original);
        catalog.SetupSequence(x => x.ListAsync(It.IsAny<CancellationToken>(), true))
            .ReturnsAsync([original]).ReturnsAsync([latest]);
        var records = Records();
        var writes = 0;
        records.Setup(x => x.CreateLaboratoryAnalysisRequestsAsync(record.Id,
                It.IsAny<CreateLaboratoryAnalysisRequestsRequest>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, CreateLaboratoryAnalysisRequestsRequest request, CancellationToken _) =>
            {
                writes++;
                request.Comment.Should().Be("Натощак");
                if (writes == 1)
                {
                    request.ExpectedVersions![original.Id].Should().Be(original.Version);
                    throw new ApiException("Справочник изменился.", HttpStatusCode.Conflict);
                }
                request.ExpectedVersions![latest.Id].Should().Be(latest.Version);
                return Task.FromResult<IReadOnlyList<LaboratoryAnalysisRequestDto>>([Request(record, latest)]);
            });
        using var panel = await PanelAsync(record, records, catalog);
        await InvokeAsync(panel, "OpenFormAsync");
        Toggle(panel, original.Id);
        Set(panel, "_comment", " Натощак ");

        await InvokeAsync(panel, "SubmitAsync");
        await InvokeAsync(panel, "SubmitAsync");
        Toggle(panel, original.Id);
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.Record), record with { Version = record.Version + 1 });
        await InvokeAsync(panel, "OnParametersSetAsync");
        await InvokeAsync(panel, "RefreshAsync");

        writes.Should().Be(1);
        Get<bool>(panel, "_catalogStale").Should().BeTrue();
        Get<IReadOnlyList<LaboratoryTestDefinitionDto>>(panel, "_catalog").Should().Equal(original);
        catalog.Verify(x => x.ListAsync(It.IsAny<CancellationToken>(), true), Times.Once);
        Get<string>(panel, "_formError").Should().Contain("выберите исследования заново");

        await InvokeAsync(panel, "LoadCatalogAsync");
        Get<HashSet<Guid>>(panel, "_selected").Should().BeEmpty();
        Get<bool>(panel, "_catalogStale").Should().BeFalse();
        Get<string>(panel, "_comment").Should().Be(" Натощак ");
        await InvokeAsync(panel, "SubmitAsync");
        writes.Should().Be(1);
        Toggle(panel, latest.Id);
        await InvokeAsync(panel, "SubmitAsync");

        writes.Should().Be(2);
        records.Verify(x => x.GetHistoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordChangesAndManualRefresh_ShouldUseEmbeddedRequesterSnapshotWithoutLoadingHistory()
    {
        var record = Record();
        var request = Request(record, Definition()) with { RequestedByNameSnapshot = "Имя врача при назначении" };
        var records = Records();
        using var panel = await PanelAsync(record with { LaboratoryAnalysisRequests = [request] }, records, Catalog());
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.Record), record with
        {
            Version = record.Version + 1, LaboratoryAnalysisRequests = [request]
        });

        await InvokeAsync(panel, "OnParametersSetAsync");
        await InvokeAsync(panel, "RefreshAsync");

        Visible(panel).Single().RequestedByNameSnapshot.Should().Be("Имя врача при назначении");
        records.VerifyNoOtherCalls();
    }

    private static MedicalRecordDto Record() => new(Guid.NewGuid(), Guid.NewGuid(), At, 1, [], []);
    private static LaboratoryTestDefinitionDto Definition(string code = "2345-7") => new(Guid.NewGuid(), code,
        "Лабораторное исследование", true, At, At, 17);
    private static LaboratoryAnalysisRequestDto Request(MedicalRecordDto record, LaboratoryTestDefinitionDto definition) =>
        new(Guid.NewGuid(), record.Id, record.PatientId, Guid.NewGuid(), definition.Id, definition.LoincCode,
            definition.DisplayName, null, At, LaboratoryAnalysisRequestStatus.Active, null);

    private static Mock<ILaboratoryTestsGateway> Catalog(params LaboratoryTestDefinitionDto[] definitions)
    {
        var mock = new Mock<ILaboratoryTestsGateway>();
        mock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>(), true)).ReturnsAsync(definitions);
        return mock;
    }

    private static Mock<IMedicalRecordsGateway> Records()
    {
        return new Mock<IMedicalRecordsGateway>(MockBehavior.Strict);
    }

    private static async Task<LaboratoryAnalysesPanel> PanelAsync(MedicalRecordDto record,
        Mock<IMedicalRecordsGateway> records, Mock<ILaboratoryTestsGateway> catalog, bool canAssign = true)
    {
        var panel = new LaboratoryAnalysesPanel();
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.Record), record);
        SetProperty(panel, nameof(LaboratoryAnalysesPanel.CanAssign), canAssign);
        SetProperty(panel, "Records", records.Object);
        SetProperty(panel, "Catalog", catalog.Object);
        await InvokeAsync(panel, "OnParametersSetAsync");
        return panel;
    }

    private static void Toggle(LaboratoryAnalysesPanel panel, Guid id) =>
        Invoke(panel, "ToggleSelection", id, new ChangeEventArgs { Value = true });
    private static IReadOnlyCollection<LaboratoryAnalysisRequestDto> Visible(LaboratoryAnalysesPanel panel) =>
        (IReadOnlyCollection<LaboratoryAnalysisRequestDto>)panel.GetType()
            .GetProperty("VisibleRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
    private static void Set(object panel, string name, object? value) => panel.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, value);
    private static void SetProperty(object panel, string name, object? value) => panel.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(panel, value);
    private static T Get<T>(object panel, string name) => (T)panel.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
    private static object? Invoke(object panel, string name, params object[] values) => panel.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, values);
    private static Task InvokeAsync(object panel, string name, params object[] values) => (Task)Invoke(panel, name, values)!;
}
