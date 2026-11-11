using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.MedicalRecords.Commands.RecordLaboratoryAnalysisResult;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Contracts.MedicalRecords;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Contracts.Patients;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class LaboratoryAnalysisRequestsApiTests(MedicalRecordsApiFactory factory)
    : IClassFixture<MedicalRecordsApiFactory>
{
    private readonly HttpClient _client = factory.CreateBusinessClient();
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateBatch_ShouldAssignSeparateIdsUseAuthenticatedDoctorAndPreserveSnapshots()
    {
        var (record, worker) = await CreateScenarioAsync();
        var catalog = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!;
        var selected = catalog.Take(2).ToArray();
        factory.Clock.SetUtcNow(T0.AddMinutes(1));
        using var created = await _client.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new { LaboratoryTestDefinitionIds = selected.Select(x => x.Id).ToArray(), Comment = " Натощак ",
                PerformedByMedicalWorkerId = Guid.NewGuid() });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location!.ToString().Should().EndWith($"/api/medical-records/{record.Id}");
        var requests = (await created.Content.ReadFromJsonAsync<LaboratoryAnalysisRequestDto[]>())!;
        requests.Should().HaveCount(2).And.OnlyContain(x => x.PatientId == record.PatientId &&
            x.MedicalRecordId == record.Id && x.RequestedByMedicalWorkerId == worker &&
            x.Comment == "Натощак" && x.Status == LaboratoryAnalysisRequestStatus.Active && x.Result == null);
        requests.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        requests.Should().OnlyContain(x => x.AuthoredAtUtc == T0.AddMinutes(1));
        requests.Should().OnlyContain(x => x.RequestedByNameSnapshot == "Проверка Алексей");
        var current = (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}"))!;
        current.Version.Should().Be(record.Version + 2);
        current.LaboratoryAnalysisRequests.Should().BeEquivalentTo(requests);

        using var repeated = await _client.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest([selected[0].Id], null));
        repeated.StatusCode.Should().Be(HttpStatusCode.Created);
        var next = (await repeated.Content.ReadFromJsonAsync<LaboratoryAnalysisRequestDto[]>())!.Single();
        requests.Select(x => x.Id).Should().NotContain(next.Id);
        next.LoincCode.Should().Be(requests[0].LoincCode);
        next.RequestedByNameSnapshot.Should().Be("Проверка Алексей");

        (await _client.PutAsJsonAsync($"/api/laboratory-tests/{selected[0].Id}",
            new SaveLaboratoryTestRequest(selected[0].LoincCode, "Название после назначения"))).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/laboratory-tests/{selected[0].Id}/active",
            new SetLaboratoryTestActiveRequest(false))).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/medical-workers/{worker}",
            new SaveMedicalWorkerRequest("Другое", "Имя", "Хирург", MedicalWorkerRole.Doctor))).EnsureSuccessStatusCode();

        var afterRename = (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}"))!;
        afterRename.LaboratoryAnalysisRequests.Should().OnlyContain(x => x.RequestedByNameSnapshot == "Проверка Алексей");
        var patientRecord = (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/patients/{record.PatientId}/medical-record"))!;
        patientRecord.LaboratoryAnalysisRequests.Should().OnlyContain(x => x.RequestedByNameSnapshot == "Проверка Алексей");

        var before = await SnapshotAsync(record.Id, T0);
        before.LaboratoryAnalysisRequests.Should().BeEmpty();
        var after = await SnapshotAsync(record.Id, T0.AddMinutes(1));
        after.LaboratoryAnalysisRequests.Should().HaveCount(3);
        after.LaboratoryAnalysisRequests.Should().OnlyContain(x => x.RequestedByNameSnapshot == "Проверка Алексей");
        after.LaboratoryAnalysisRequests.Should().OnlyContain(x => x.Result == null &&
            x.Status == LaboratoryAnalysisRequestStatus.Active);
        after.LaboratoryAnalysisRequests.Single(x => x.Id == requests[0].Id).DisplayName.Should()
            .Be(selected[0].DisplayName);
        var history = (await _client.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(
            $"/api/medical-records/{record.Id}/history"))!;
        history.Where(x => x.EventType == "LaboratoryAnalysisRequested").Should().HaveCount(3)
            .And.OnlyContain(x => x.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
                x.PerformedByMedicalWorkerId == worker && x.PerformedByServiceAccountId == null);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("empty-id")]
    [InlineData("over-limit")]
    [InlineData("long-comment")]
    [InlineData("nul-comment")]
    public async Task CreateBatch_InvalidSelection_ShouldNotChangeRecordOrHistory(string kind)
    {
        var (record, _) = await CreateScenarioAsync();
        using var added = await _client.PostAsJsonAsync("/api/laboratory-tests",
            new SaveLaboratoryTestRequest(BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()) + "-0", "Анализ для набора"));
        added.EnsureSuccessStatusCode();
        var active = (await added.Content.ReadFromJsonAsync<LaboratoryTestDefinitionDto>())!;
        var inactive = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!
            .First(x => x.Id != active.Id);
        if (kind == "inactive")
            (await _client.PutAsJsonAsync($"/api/laboratory-tests/{inactive.Id}/active",
                new SetLaboratoryTestActiveRequest(false))).EnsureSuccessStatusCode();
        IReadOnlyList<Guid>? ids = kind switch
        {
            "missing" => [active.Id, Guid.NewGuid()],
            "inactive" => [active.Id, inactive.Id],
            "duplicate" => [active.Id, active.Id],
            "empty" => [],
            "null" => null,
            "empty-id" => [Guid.Empty],
            "over-limit" => Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(),
            _ => [active.Id]
        };
        var comment = kind switch { "long-comment" => new string('Я', 2001), "nul-comment" => "Текст\0комментария", _ => null };
        var historyBefore = await _client.GetStringAsync($"/api/medical-records/{record.Id}/history");

        using var response = await _client.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new { LaboratoryTestDefinitionIds = ids, Comment = comment });

        response.StatusCode.Should().Be(kind == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}"))
            .Should().BeEquivalentTo(record);
        (await _client.GetStringAsync($"/api/medical-records/{record.Id}/history")).Should().Be(historyBefore);
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().LaboratoryAnalysisRequests
            .AnyAsync(x => x.MedicalRecordId == record.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task CreateBatch_WithoutCsrf_ShouldRejectWithoutMutation()
    {
        var (record, _) = await CreateScenarioAsync();
        var definition = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!.First();
        _client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        (await _client.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest([definition.Id], null))).StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);

        (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}"))
            .Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task RecordResult_WithoutCurrentServiceIdentity_ShouldFailClosedBeforeAnyMutation()
    {
        var (record, _) = await CreateScenarioAsync();
        var catalog = (await _client.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!;
        using var created = await _client.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest([catalog[0].Id], null));
        created.EnsureSuccessStatusCode();
        var request = (await created.Content.ReadFromJsonAsync<LaboratoryAnalysisRequestDto[]>())!.Single();
        var before = (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}"))!;
        await using var scope = factory.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Func<Task> write = async () => await sender.Send(new RecordLaboratoryAnalysisResultCommand(
            request.Id, record.PatientId, request.LoincCode, "5.6", T0));

        await write.Should().ThrowAsync<ServiceAccountAccessDeniedException>();
        (await _client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}"))
            .Should().BeEquivalentTo(before);
    }

    private async Task<(MedicalRecordDto Record, Guid Worker)> CreateScenarioAsync()
    {
        factory.Clock.SetUtcNow(T0);
        using var workerResponse = await _client.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Алексей", "Проверка", "Врач", MedicalWorkerRole.Doctor));
        workerResponse.EnsureSuccessStatusCode();
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        _client.DefaultRequestHeaders.Add("X-Medical-Worker-Id", worker.Id.ToString());
        using var patientResponse = await _client.PostAsJsonAsync("/api/patients",
            new SavePatientRequest("Пациент", "Проверка", null, new DateOnly(1990, 1, 1), AdministrativeSex.Male, null));
        patientResponse.EnsureSuccessStatusCode();
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDto>())!;
        using var recordResponse = await _client.PostAsync($"/api/patients/{patient.Id}/medical-record", null);
        recordResponse.EnsureSuccessStatusCode();
        return ((await recordResponse.Content.ReadFromJsonAsync<MedicalRecordDto>())!, worker.Id);
    }

    private async Task<MedicalRecordSnapshotDto> SnapshotAsync(Guid id, DateTimeOffset at) =>
        (await _client.GetFromJsonAsync<MedicalRecordSnapshotDto>(
            $"/api/medical-records/{id}/snapshot?at={Uri.EscapeDataString(at.ToString("O"))}"))!;
}
