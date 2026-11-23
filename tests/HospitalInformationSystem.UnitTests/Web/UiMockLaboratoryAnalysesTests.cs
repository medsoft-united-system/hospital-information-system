using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Development;
using MedicalWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class UiMockLaboratoryAnalysesTests
{
    [Fact]
    public async Task Catalog_ShouldProvideSevenDistinctSourcedDefinitionsAndFilterActivity()
    {
        using var fixture = new Fixture(UiMockPersona.Administrator);
        var catalog = (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests"))!;
        catalog.Select(x => x.LoincCode).Should().BeEquivalentTo(
            ["2345-7", "718-7", "7918-6", "5196-1", "13955-0", "5393-4", "101363-0"]);
        catalog.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        catalog.Should().OnlyContain(x => x.IsActive && x.CreatedAtUtc != default && x.UpdatedAtUtc >= x.CreatedAtUtc && x.Version > 0);
        var definition = catalog.Single(x => x.LoincCode == "718-7");
        (await fixture.Http.PutAsJsonAsync($"api/laboratory-tests/{definition.Id}/active",
            new SetLaboratoryTestActiveRequest(false, definition.Version))).EnsureSuccessStatusCode();

        (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests"))!
            .Should().NotContain(x => x.Id == definition.Id);
        (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests?activeOnly=false"))!
            .Should().Contain(x => x.Id == definition.Id && !x.IsActive);
        (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto>($"api/laboratory-tests/{definition.Id}"))!
            .IsActive.Should().BeFalse();
        (await fixture.Http.DeleteAsync($"api/laboratory-tests/{definition.Id}")).StatusCode.Should()
            .Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task CreateBatch_ShouldUseCurrentDoctorAndProduceIndependentRequestsAndHistoricalState()
    {
        using var fixture = new Fixture(UiMockPersona.Doctor);
        var definitions = (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests"))!
            .Take(2).ToArray();
        var path = $"api/medical-records/{UiMockState.MedicalRecordId}";
        var before = (await fixture.Http.GetFromJsonAsync<MedicalRecordDto>(path))!;
        using var response = await fixture.Http.PostAsJsonAsync(path + "/laboratory-analysis-requests", new
        {
            LaboratoryTestDefinitionIds = definitions.Select(x => x.Id).ToArray(),
            Comment = " Контроль утром ",
            ExpectedVersions = definitions.ToDictionary(x => x.Id, x => x.Version),
            RequestedByMedicalWorkerId = UiMockState.NurseId
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<LaboratoryAnalysisRequestDto[]>())!;
        created.Should().HaveCount(2).And.OnlyContain(x => x.RequestedByMedicalWorkerId == UiMockState.DoctorId
            && x.PatientId == before.PatientId && x.MedicalRecordId == before.Id
            && x.Comment == "Контроль утром" && x.Result == null && x.Status == LaboratoryAnalysisRequestStatus.Active
            && x.RequestedByNameSnapshot == "Учебный Алексей");
        created.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        var current = (await fixture.Http.GetFromJsonAsync<MedicalRecordDto>(path))!;
        current.Version.Should().Be(before.Version + 2);
        current.LaboratoryAnalysisRequests.Should().BeEquivalentTo(created);
        var history = (await fixture.Http.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(path + "/history"))!;
        history.Skip(1).Should().OnlyContain(x => x.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
            x.PerformedByMedicalWorkerId == UiMockState.DoctorId && x.EventType == "LaboratoryAnalysisRequested");
        (await Snapshot(fixture, before.Id, before.CreatedAtUtc)).LaboratoryAnalysisRequests.Should().BeEmpty();
        (await Snapshot(fixture, before.Id, created[0].AuthoredAtUtc)).LaboratoryAnalysisRequests.Should()
            .BeEquivalentTo(created);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("inactive", HttpStatusCode.BadRequest)]
    [InlineData("duplicate", HttpStatusCode.BadRequest)]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    [InlineData("null", HttpStatusCode.BadRequest)]
    [InlineData("empty-id", HttpStatusCode.BadRequest)]
    [InlineData("long-comment", HttpStatusCode.BadRequest)]
    [InlineData("nul-comment", HttpStatusCode.BadRequest)]
    public async Task CreateBatch_InvalidLastSelection_ShouldLeaveRecordAndHistoryUnchanged(string kind,
        HttpStatusCode expected)
    {
        using var fixture = new Fixture(UiMockPersona.Administrator);
        var definitions = (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests"))!;
        if (kind == "inactive")
            (await fixture.Http.PutAsJsonAsync($"api/laboratory-tests/{definitions[1].Id}/active",
                new SetLaboratoryTestActiveRequest(false, definitions[1].Version))).EnsureSuccessStatusCode();
        definitions = (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests?activeOnly=false"))!;
        fixture.State.SwitchPersona(UiMockPersona.Doctor);
        IReadOnlyList<Guid>? ids = kind switch
        {
            "missing" => [definitions[0].Id, Guid.NewGuid()],
            "inactive" => [definitions[0].Id, definitions[1].Id],
            "duplicate" => [definitions[0].Id, definitions[0].Id],
            "empty" => [],
            "null" => null,
            "empty-id" => [Guid.Empty],
            _ => [definitions[0].Id]
        };
        var path = $"api/medical-records/{UiMockState.MedicalRecordId}";
        var before = await fixture.Http.GetStringAsync(path);
        var history = await fixture.Http.GetStringAsync(path + "/history");
        var comment = kind switch { "long-comment" => new string('Я', 2001), "nul-comment" => "Текст\0комментария", _ => null };

        using var response = await fixture.Http.PostAsJsonAsync(path + "/laboratory-analysis-requests",
            new
            {
                LaboratoryTestDefinitionIds = ids,
                Comment = comment,
                ExpectedVersions = ids?.Distinct().ToDictionary(id => id,
                    id => definitions.FirstOrDefault(x => x.Id == id)?.Version ?? 1)
            });

        response.StatusCode.Should().Be(expected);
        (await fixture.Http.GetStringAsync(path)).Should().Be(before);
        (await fixture.Http.GetStringAsync(path + "/history")).Should().Be(history);
    }

    [Theory]
    [InlineData(UiMockPersona.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(UiMockPersona.Administrator, HttpStatusCode.Forbidden)]
    [InlineData(UiMockPersona.Nurse, HttpStatusCode.Forbidden)]
    [InlineData(UiMockPersona.LaboratoryTechnician, HttpStatusCode.Forbidden)]
    [InlineData(UiMockPersona.Other, HttpStatusCode.Forbidden)]
    public async Task CreateBatch_NonDoctor_ShouldDenyMutation(UiMockPersona persona, HttpStatusCode expected)
    {
        using var fixture = new Fixture(persona);
        using var response = await fixture.Http.PostAsJsonAsync(
            $"api/medical-records/{UiMockState.MedicalRecordId}/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest([Guid.NewGuid()], null));

        response.StatusCode.Should().Be(expected);
        if (persona is not (UiMockPersona.Anonymous or UiMockPersona.Administrator))
            (await fixture.Http.GetFromJsonAsync<MedicalRecordDto>($"api/medical-records/{UiMockState.MedicalRecordId}"))!
                .LaboratoryAnalysisRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task SeededLaboratoryRecord_ShouldShowResultOnlyAtHisReceiptAndPreserveOriginalSnapshots()
    {
        using var fixture = new Fixture(UiMockPersona.Doctor);
        var path = $"api/medical-records/{UiMockState.LaboratoryMedicalRecordId}";
        var current = (await fixture.Http.GetFromJsonAsync<MedicalRecordDto>(path))!;
        current.LaboratoryAnalysisRequests.Should().HaveCount(2);
        current.LaboratoryAnalysisRequests.Should().OnlyContain(x => x.RequestedByNameSnapshot == "Учебный Алексей");
        var completed = current.LaboratoryAnalysisRequests.Single(x => x.Id == UiMockState.LaboratoryCompletedRequestId);
        var active = current.LaboratoryAnalysisRequests.Single(x => x.Id == UiMockState.LaboratoryActiveRequestId);
        completed.Status.Should().Be(LaboratoryAnalysisRequestStatus.Completed);
        completed.Result!.Value.Should().Be("5.6");
        completed.Result.Id.Should().NotBe(completed.Id);
        active.Status.Should().Be(LaboratoryAnalysisRequestStatus.Active);
        active.Result.Should().BeNull();
        var history = (await fixture.Http.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(path + "/history"))!;
        var serviceEvent = history.Single(x => x.EventType == "LaboratoryAnalysisResultRecorded");
        serviceEvent.AuthorKind.Should().Be(MedicalRecordAuthorKind.ServiceAccount);
        serviceEvent.PerformedByServiceAccountId.Should().Be(UiMockState.LaboratoryServiceId);
        serviceEvent.PerformedByMedicalWorkerId.Should().BeNull();

        (await Snapshot(fixture, current.Id, current.CreatedAtUtc)).LaboratoryAnalysisRequests.Should().BeEmpty();
        var between = await Snapshot(fixture, current.Id, completed.Result.IssuedAtUtc);
        between.LaboratoryAnalysisRequests.Single(x => x.Id == completed.Id).Result.Should().BeNull();
        between.LaboratoryAnalysisRequests.Single(x => x.Id == completed.Id).Status.Should()
            .Be(LaboratoryAnalysisRequestStatus.Active);
        var after = await Snapshot(fixture, current.Id, completed.Result.ReceivedAtUtc);
        after.LaboratoryAnalysisRequests.Single(x => x.Id == completed.Id).Result!.Value.Should().Be("5.6");

        fixture.State.SwitchPersona(UiMockPersona.Administrator);
        var definition = (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto>(
            $"api/laboratory-tests/{completed.LaboratoryTestDefinitionId}"))!;
        (await fixture.Http.PutAsJsonAsync($"api/laboratory-tests/{completed.LaboratoryTestDefinitionId}",
            new SaveLaboratoryTestRequest(completed.LoincCode, "Изменённый справочник", definition.Version))).EnsureSuccessStatusCode();
        (await fixture.Http.PutAsJsonAsync($"api/medical-workers/{UiMockState.DoctorId}",
            new SaveMedicalWorkerRequest("Другое", "Имя", "Врач", MedicalWorkerRole.Doctor))).EnsureSuccessStatusCode();
        fixture.State.SwitchPersona(UiMockPersona.Nurse);
        var unchanged = (await fixture.Http.GetFromJsonAsync<MedicalRecordDto>(path))!;
        unchanged.LaboratoryAnalysisRequests.Single(x => x.Id == completed.Id).DisplayName.Should().Be(completed.DisplayName);
        unchanged.LaboratoryAnalysisRequests.Should().OnlyContain(x => x.RequestedByNameSnapshot == "Учебный Алексей");
        (await Snapshot(fixture, current.Id, completed.Result.ReceivedAtUtc)).Should().BeEquivalentTo(after);
    }

    [Fact]
    public async Task CatalogVersions_ShouldRejectStaleStatusChangeAndStaleBatchWithoutPartialMutation()
    {
        using var fixture = new Fixture(UiMockPersona.Administrator);
        var initial = (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("api/laboratory-tests"))!
            .Take(2).ToArray();
        using var updatedResponse = await fixture.Http.PutAsJsonAsync($"api/laboratory-tests/{initial[1].Id}",
            new SaveLaboratoryTestRequest(initial[1].LoincCode, "Уточнённое наименование", initial[1].Version));
        updatedResponse.EnsureSuccessStatusCode();
        var latest = (await updatedResponse.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        latest.Version.Should().BeGreaterThan(initial[1].Version);
        (await fixture.Http.PutAsJsonAsync($"api/laboratory-tests/{latest.Id}/active",
            new SetLaboratoryTestActiveRequest(true, initial[1].Version))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        fixture.State.SwitchPersona(UiMockPersona.Doctor);
        var path = $"api/medical-records/{UiMockState.MedicalRecordId}";
        var before = await fixture.Http.GetStringAsync(path);
        var historyBefore = await fixture.Http.GetStringAsync(path + "/history");

        using var rejected = await fixture.Http.PostAsJsonAsync(path + "/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest(initial.Select(x => x.Id).ToArray(), null,
                initial.ToDictionary(x => x.Id, x => x.Version)));

        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await fixture.Http.GetStringAsync(path)).Should().Be(before);
        (await fixture.Http.GetStringAsync(path + "/history")).Should().Be(historyBefore);
        (await fixture.Http.GetFromJsonAsync<LaboratoryTestDefinitionDto>($"api/laboratory-tests/{latest.Id}"))!
            .Should().Be(latest);
    }

    [Fact]
    public async Task ResultWriteEndpoint_ShouldRemainUnavailableInHumanMockApi()
    {
        using var fixture = new Fixture(UiMockPersona.Doctor);
        (await fixture.Http.PostAsJsonAsync(
            $"api/medical-records/{UiMockState.LaboratoryMedicalRecordId}/laboratory-analysis-results",
            new { Value = "42" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<MedicalRecordSnapshotDto> Snapshot(Fixture fixture, Guid id, DateTimeOffset at) =>
        (await fixture.Http.GetFromJsonAsync<MedicalRecordSnapshotDto>(
            $"api/medical-records/{id}/snapshot?at={Uri.EscapeDataString(at.ToString("O"))}"))!;

    private sealed class Fixture : IDisposable
    {
        public UiMockState State { get; }
        public HttpClient Http { get; }

        public Fixture(UiMockPersona persona)
        {
            var session = new SessionState();
            State = new UiMockState(session) { EnableLiveAudit = false };
            State.SwitchPersona(persona);
            Http = new HttpClient(new BrowserSessionHandler(session, new AntiforgeryTokenStore(),
                new AuthenticationOperationCoordinator())
            { InnerHandler = new UiMockHttpMessageHandler(State) })
            { BaseAddress = new Uri("https://ui-mock.invalid/") };
        }

        public void Dispose() => Http.Dispose();
    }
}
