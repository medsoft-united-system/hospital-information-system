using System.Net;
using System.Reflection;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Components.MedicalRecords;
using HospitalInformationSystem.Web.Gateways;
using Microsoft.AspNetCore.Components;
using Moq;
using MedicalRecord = HospitalInformationSystem.Web.Pages.Medical.MedicalRecord;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class MedicalRecordLaboratoryPageTests
{
    [Fact]
    public async Task ConfirmedOrders_ShouldRemainVisibleIfFollowingReadFailsAndInvalidateHistory()
    {
        var record = Record();
        var created = Request(record);
        var records = new Mock<IMedicalRecordsGateway>();
        records.Setup(gateway => gateway.FindForPatientAsync(record.PatientId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Недоступен", HttpStatusCode.ServiceUnavailable));
        using var page = Page(record, records.Object);
        Set(page, "_history", Array.Empty<MedicalRecordHistoryEventDto>());

        await InvokeAsync(page, "OnLaboratoryRequestsCreatedAsync", (object)new[] { created });

        Get<MedicalRecordDto>(page, "_record").LaboratoryAnalysisRequests.Should().Equal(created);
        Get<IReadOnlyList<MedicalRecordHistoryEventDto>?>(page, "_history").Should().BeNull();
        Get<string>(page, "_actionNotice").Should().Contain("сохранено");
        Get<MedicalRecordDto>(page, "_record").Version.Should().Be(record.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdersFromAnotherPatientOrRecord_ShouldNeverBeApplied(bool anotherPatient)
    {
        var record = Record();
        var foreign = anotherPatient ? Request(record) with { PatientId = Guid.NewGuid() }
            : Request(record) with { MedicalRecordId = Guid.NewGuid() };
        var records = new Mock<IMedicalRecordsGateway>(MockBehavior.Strict);
        using var page = Page(record, records.Object);

        await InvokeAsync(page, "OnLaboratoryRequestsCreatedAsync", (object)new[] { foreign });

        Get<MedicalRecordDto>(page, "_record").Should().BeSameAs(record);
        records.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LateRefreshAfterChangingPatient_ShouldNotOverwriteNewRecord()
    {
        var record = Record();
        var pending = new TaskCompletionSource<MedicalRecordDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new Mock<IMedicalRecordsGateway>();
        records.Setup(gateway => gateway.FindForPatientAsync(record.PatientId, It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        using var page = Page(record, records.Object);
        var refresh = InvokeAsync(page, "RefreshLaboratoryRecordAsync");
        Invoke(page, "BeginNavigation");
        var next = Record();
        SetParameter(page, "PatientId", next.PatientId);
        Set(page, "_record", next);

        pending.SetResult(record);
        await refresh;

        Get<MedicalRecordDto>(page, "_record").Should().BeSameAs(next);
        Get<bool>(page, "_laboratoryBusy").Should().BeFalse();
        Get<bool>(page, "_busy").Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ShouldShowNewResultAndKeepSelectedHistoricalSnapshotUnchanged()
    {
        var empty = Record();
        var active = Request(empty) with { RequestedByNameSnapshot = "Имя при назначении" };
        var record = empty with { LaboratoryAnalysisRequests = [active] };
        var completed = active with
        {
            Status = LaboratoryAnalysisRequestStatus.Completed,
            Result = new LaboratoryAnalysisResultDto(Guid.NewGuid(), active.Id, record.PatientId, "142",
                active.AuthoredAtUtc.AddMinutes(1), active.AuthoredAtUtc.AddMinutes(2), Guid.NewGuid())
        };
        var latest = record with { Version = record.Version + 1, LaboratoryAnalysisRequests = [completed] };
        var snapshot = new MedicalRecordSnapshotDto(record.Id, record.PatientId, active.AuthoredAtUtc,
            record.Version, [], [])
        { LaboratoryAnalysisRequests = [active] };
        var records = new Mock<IMedicalRecordsGateway>();
        records.Setup(gateway => gateway.FindForPatientAsync(record.PatientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latest);
        using var page = Page(record, records.Object);
        Set(page, "_snapshot", snapshot);

        await InvokeAsync(page, "RefreshLaboratoryRecordAsync");

        Get<MedicalRecordDto>(page, "_record").LaboratoryAnalysisRequests.Single().Result!.Value.Should().Be("142");
        Get<MedicalRecordSnapshotDto>(page, "_snapshot").Should().BeSameAs(snapshot);
        snapshot.LaboratoryAnalysisRequests.Single().Result.Should().BeNull();
        snapshot.LaboratoryAnalysisRequests.Single().RequestedByNameSnapshot.Should().Be("Имя при назначении");
    }

    [Fact]
    public async Task PanelRefreshCallback_ShouldLoadRecordWhilePanelOwnsBusyState()
    {
        var record = Record();
        var latest = record with { Version = record.Version + 1 };
        var records = new Mock<IMedicalRecordsGateway>();
        records.Setup(gateway => gateway.FindForPatientAsync(record.PatientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latest);
        records.Setup(gateway => gateway.GetHistoryAsync(record.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MedicalRecordHistoryEventDto>());
        using var page = Page(record, records.Object);
        using var panel = new LaboratoryAnalysesPanel();
        SetParameter(panel, "Record", record);
        Set(panel, "_contextRecordId", record.Id);
        Set(panel, "_cts", new CancellationTokenSource());
        panel.GetType().GetProperty("Records", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, records.Object);
        SetParameter(panel, "BusyChanged", EventCallback.Factory.Create<bool>(new object(),
            (bool busy) => Invoke(page, "OnLaboratoryBusyChanged", busy)));
        SetParameter(panel, "RefreshRequested", EventCallback.Factory.Create(new object(),
            () => InvokeAsync(page, "RefreshLaboratoryRecordAsync")));

        await InvokeAsync(panel, "RefreshAsync");

        records.Verify(gateway => gateway.FindForPatientAsync(record.PatientId, It.IsAny<CancellationToken>()), Times.Once);
        Get<MedicalRecordDto>(page, "_record").Should().BeSameAs(latest);
        Get<bool>(page, "_laboratoryBusy").Should().BeFalse();
        Get<bool>(page, "_busy").Should().BeFalse();
    }

    [Fact]
    public async Task FailedRefresh_ShouldKeepClinicalDataAndReleaseBusyState()
    {
        var record = Record();
        var records = new Mock<IMedicalRecordsGateway>();
        records.Setup(gateway => gateway.FindForPatientAsync(record.PatientId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException());
        using var page = Page(record, records.Object);

        await InvokeAsync(page, "RefreshLaboratoryRecordAsync");

        Get<MedicalRecordDto>(page, "_record").Should().BeSameAs(record);
        Get<bool>(page, "_busy").Should().BeFalse();
        Get<string>(page, "_actionError").Should().Contain("Не удалось обновить");
    }

    [Fact]
    public async Task LaboratoryWrite_ShouldBlockOtherRecordMutations()
    {
        using var page = Page(Record(), new Mock<IMedicalRecordsGateway>().Object);
        Set(page, "_laboratoryBusy", true);
        var writes = 0;

        await InvokeAsync(page, "RunMutationAsync", (Func<CancellationToken, Task>)(_ =>
        {
            writes++;
            return Task.CompletedTask;
        }), false);

        writes.Should().Be(0);
    }

    private static MedicalRecord Page(MedicalRecordDto record, IMedicalRecordsGateway gateway)
    {
        var page = new MedicalRecord();
        SetParameter(page, "PatientId", record.PatientId);
        page.GetType().GetProperty("Records", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, gateway);
        Set(page, "_record", record);
        Set(page, "_cts", new CancellationTokenSource());
        return page;
    }

    private static MedicalRecordDto Record() => new(Guid.NewGuid(), Guid.NewGuid(),
        new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), 1, [], []);
    private static LaboratoryAnalysisRequestDto Request(MedicalRecordDto record) => new(Guid.NewGuid(), record.Id,
        record.PatientId, Guid.NewGuid(), Guid.NewGuid(), "718-7", "Гемоглобин", null,
        record.CreatedAtUtc, LaboratoryAnalysisRequestStatus.Active, null);
    private static void SetParameter(object page, string property, object value) =>
        page.GetType().GetProperty(property)!.SetValue(page, value);
    private static void Set(object page, string name, object? value) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T Get<T>(object page, string name) =>
        (T)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static object? Invoke(object page, string method, params object[] args) =>
        page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static Task InvokeAsync(object page, string method, params object[] args) => (Task)Invoke(page, method, args)!;
}
