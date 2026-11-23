using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MedicalWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;
using Observation = Hl7.Fhir.Model.Observation;
using Practitioner = Hl7.Fhir.Model.Practitioner;
using Resource = Hl7.Fhir.Model.Resource;
using ServiceRequest = Hl7.Fhir.Model.ServiceRequest;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab3AcceptanceTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    [Fact]
    public async Task RealHumanAndServiceTokens_ResultShouldReachEveryMedicalReaderAndHistoricalSnapshot()
    {
        using var administrator = await factory.AdministratorAsync();
        var authenticated = await ClinicianAsync(administrator, MedicalWorkerRole.Doctor);
        using var doctor = authenticated.Client;
        var scenario = await ScenarioAsync(doctor, authenticated.Worker.Id);
        using var machine = factory.CreateMachineClient(scenario.ServiceAccess);
        var result = await CompleteAsync(machine, scenario.Requests[0], "5.6");
        var expected = (await doctor.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{scenario.Record.Id}"))!;
        var history = (await doctor.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(
            $"/api/medical-records/{scenario.Record.Id}/history"))!;

        expected.LaboratoryAnalysisRequests.Should().HaveCount(4);
        expected.LaboratoryAnalysisRequests.Single(request => request.Id == scenario.Requests[0].Id)
            .Result!.Id.Should().Be(Guid.Parse(result.Id!));
        expected.LaboratoryAnalysisRequests.Where(request => request.Id != scenario.Requests[0].Id)
            .Should().OnlyContain(request => request.Status == LaboratoryAnalysisRequestStatus.Active && request.Result == null);
        history.Where(entry => entry.EventType == "LaboratoryAnalysisRequested").Should().HaveCount(4)
            .And.OnlyContain(entry => entry.AuthorKind == MedicalRecordAuthorKind.MedicalWorker &&
                entry.PerformedByMedicalWorkerId == authenticated.Worker.Id && entry.PerformedByServiceAccountId == null);
        var resultEvent = history.Single(entry => entry.EventType == "LaboratoryAnalysisResultRecorded");
        resultEvent.AuthorKind.Should().Be(MedicalRecordAuthorKind.ServiceAccount);
        resultEvent.PerformedByServiceAccountId.Should().Be(scenario.ServiceAccountId);
        resultEvent.PerformedByMedicalWorkerId.Should().BeNull();

        foreach (var role in Enum.GetValues<MedicalWorkerRole>())
        {
            var reader = await ClinicianAsync(administrator, role);
            using var client = reader.Client;
            (await client.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{scenario.Record.Id}"))
                .Should().BeEquivalentTo(expected);
            (await client.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(
                $"/api/medical-records/{scenario.Record.Id}/history")).Should().BeEquivalentTo(history);
            var before = await SnapshotAsync(client, scenario.Record.Id, scenario.BeforeResult);
            before.LaboratoryAnalysisRequests.Should().HaveCount(4).And.OnlyContain(request =>
                request.Status == LaboratoryAnalysisRequestStatus.Active && request.Result == null);
            var after = await SnapshotAsync(client, scenario.Record.Id, resultEvent.OccurredAtUtc);
            after.LaboratoryAnalysisRequests.Should().BeEquivalentTo(expected.LaboratoryAnalysisRequests);
            after.Version.Should().Be(expected.Version);
            if (role == MedicalWorkerRole.Doctor) continue;

            using var denied = await client.PostAsJsonAsync(
                $"/api/medical-records/{scenario.Record.Id}/laboratory-analysis-requests",
                new CreateLaboratoryAnalysisRequestsRequest([scenario.Definitions[0].Id], "Не должно сохраниться",
                    new Dictionary<Guid, uint> { [scenario.Definitions[0].Id] = scenario.Definitions[0].Version }));
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        foreach (var route in new[] { $"/api/medical-records/{scenario.Record.Id}",
                     $"/api/medical-records/{scenario.Record.Id}/history",
                     SnapshotUrl(scenario.Record.Id, resultEvent.OccurredAtUtc) })
            using (var denied = await administrator.GetAsync(route))
                denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await doctor.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{scenario.Record.Id}"))
            .Should().BeEquivalentTo(expected);
        (await doctor.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(
            $"/api/medical-records/{scenario.Record.Id}/history")).Should().BeEquivalentTo(history);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.LaboratoryAnalysisRequests.CountAsync(request => request.MedicalRecordId == scenario.Record.Id))
            .Should().Be(4);
        (await db.LaboratoryAnalysisResults.CountAsync(resultRow => resultRow.PatientId == scenario.Record.PatientId))
            .Should().Be(1);
    }

    [Fact]
    public async Task RealServiceToken_SoftDeletedRequesterShouldRemainResolvableWithoutRewritingClinicalSnapshots()
    {
        using var administrator = await factory.AdministratorAsync();
        var authenticated = await ClinicianAsync(administrator, MedicalWorkerRole.Doctor);
        using var doctor = authenticated.Client;
        var scenario = await ScenarioAsync(doctor, authenticated.Worker.Id);
        using var machine = factory.CreateMachineClient(scenario.ServiceAccess);
        var originalName = authenticated.Worker.FirstName;
        var definition = scenario.Definitions.Single(item => item.Id == scenario.Requests[0].LaboratoryTestDefinitionId);
        using var changedDefinition = await administrator.PutAsJsonAsync($"/api/laboratory-tests/{definition.Id}",
            new SaveLaboratoryTestRequest(definition.LoincCode, "Новое имя справочника после назначения", definition.Version));
        changedDefinition.EnsureSuccessStatusCode();
        using var renamed = await administrator.PutAsJsonAsync($"/api/medical-workers/{authenticated.Worker.Id}",
            new SaveMedicalWorkerRequest("Изменённое", "Имя", "Врач-хирург", MedicalWorkerRole.Doctor));
        renamed.EnsureSuccessStatusCode();
        using var deleted = await administrator.DeleteAsync($"/api/medical-workers/{authenticated.Worker.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var requesterResponse = await machine.GetAsync($"/fhir/Practitioner/{authenticated.Worker.Id:D}");
        var requester = await FhirAsync<Practitioner>(requesterResponse);
        requester.Active.Should().BeFalse();
        using var orderResponse = await machine.GetAsync($"/fhir/ServiceRequest/{scenario.Requests[0].Id:D}");
        var order = await FhirAsync<ServiceRequest>(orderResponse);
        order.Requester!.Reference.Should().Be($"Practitioner/{authenticated.Worker.Id:D}");
        order.Code!.Concept!.Coding.Single().Display.Should().Be(scenario.Requests[0].DisplayName);
        var result = await CompleteAsync(machine, scenario.Requests[0], "142");
        result.Code!.Coding.Single().Display.Should().Be(scenario.Requests[0].DisplayName);
        var reader = await ClinicianAsync(administrator, MedicalWorkerRole.Nurse);
        using var nurse = reader.Client;
        var current = (await nurse.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{scenario.Record.Id}"))!;
        current.LaboratoryAnalysisRequests.Single(request => request.Id == scenario.Requests[0].Id)
            .DisplayName.Should().Be(scenario.Requests[0].DisplayName);
        var history = (await nurse.GetFromJsonAsync<MedicalRecordHistoryEventDto[]>(
            $"/api/medical-records/{scenario.Record.Id}/history"))!;
        history.Where(entry => entry.EventType == "LaboratoryAnalysisRequested").Should()
            .OnlyContain(entry => entry.PerformedByName.Contains(originalName, StringComparison.Ordinal) &&
                !entry.PerformedByName.Contains("Изменённое", StringComparison.Ordinal));
        var before = await SnapshotAsync(nurse, scenario.Record.Id, scenario.BeforeResult);
        before.LaboratoryAnalysisRequests.Should().HaveCount(4).And.OnlyContain(request => request.Result == null);
        before.LaboratoryAnalysisRequests.Single(request => request.Id == scenario.Requests[0].Id).DisplayName
            .Should().Be(scenario.Requests[0].DisplayName);
    }

    private async Task<Scenario> ScenarioAsync(HttpClient doctor, Guid doctorId)
    {
        var definitions = (await doctor.GetFromJsonAsync<LaboratoryTestDefinitionDto[]>("/api/laboratory-tests"))!
            .Where(definition => definition.IsActive).Take(3).ToArray();
        definitions.Should().HaveCount(3);
        using var patientResponse = await doctor.PostAsJsonAsync("/api/patients", new SavePatientRequest(
            "Учебный", "Пациент", null, new DateOnly(1990, 1, 1), AdministrativeSex.Male, "Не экспортировать в FHIR"));
        patientResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDto>())!;
        using var recordResponse = await doctor.PostAsync($"/api/patients/{patient.Id}/medical-record", null);
        recordResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = (await recordResponse.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
        factory.AdvanceTime(TimeSpan.FromSeconds(1));
        using var assigned = await doctor.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest(definitions.Select(definition => definition.Id).ToArray(), "Натощак",
                definitions.ToDictionary(definition => definition.Id, definition => definition.Version)));
        assigned.StatusCode.Should().Be(HttpStatusCode.Created);
        var requests = (await assigned.Content.ReadFromJsonAsync<LaboratoryAnalysisRequestDto[]>())!;
        requests.Should().HaveCount(3).And.OnlyContain(request => request.RequestedByMedicalWorkerId == doctorId &&
            request.PatientId == patient.Id && request.Status == LaboratoryAnalysisRequestStatus.Active);
        requests.Select(request => request.Id).Should().OnlyHaveUniqueItems();
        requests.Select(request => request.LoincCode).Should().BeEquivalentTo(definitions.Select(definition => definition.LoincCode));
        using var repeated = await doctor.PostAsJsonAsync($"/api/medical-records/{record.Id}/laboratory-analysis-requests",
            new CreateLaboratoryAnalysisRequestsRequest([requests[0].LaboratoryTestDefinitionId], "Повторный заказ",
                definitions.Where(definition => definition.Id == requests[0].LaboratoryTestDefinitionId)
                    .ToDictionary(definition => definition.Id, definition => definition.Version)));
        repeated.StatusCode.Should().Be(HttpStatusCode.Created);
        var repeat = (await repeated.Content.ReadFromJsonAsync<LaboratoryAnalysisRequestDto[]>())!.Single();
        repeat.LoincCode.Should().Be(requests[0].LoincCode);
        requests.Select(request => request.Id).Should().NotContain(repeat.Id);
        var account = await factory.CreateAccountAsync();
        var pair = await factory.IssueAsync(account.ApiKey);
        var before = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        factory.AdvanceTime(TimeSpan.FromSeconds(1));
        return new Scenario(record, requests, definitions, before, account.Account.Id, pair.AccessToken);
    }

    private async Task<(HttpClient Client, MedicalWorkerDto Worker)> ClinicianAsync(HttpClient administrator,
        MedicalWorkerRole role)
    {
        using var workerResponse = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Алексей", "Приёмочный", "Сотрудник", role));
        workerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var worker = (await workerResponse.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
        using var accountResponse = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("lab3." + Guid.NewGuid().ToString("N")[..11]));
        accountResponse.EnsureSuccessStatusCode();
        var account = (await accountResponse.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
        var client = factory.Application.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
        try
        {
            await ServiceAccountsApiTests.SetCsrfAsync(client);
            using var login = await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest(account.Account.UserName, account.GeneratedPassword));
            login.EnsureSuccessStatusCode();
            await ServiceAccountsApiTests.SetCsrfAsync(client);
            return (client, worker);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<Observation> CompleteAsync(HttpClient machine, LaboratoryAnalysisRequestDto request, string value)
    {
        var issued = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var body = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["status"] = "final",
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                { ["system"] = "http://loinc.org", ["code"] = request.LoincCode })
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{request.PatientId:D}" },
            ["basedOn"] = new JsonArray(new JsonObject { ["reference"] = $"ServiceRequest/{request.Id:D}" }),
            ["issued"] = issued.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["valueString"] = value
        };
        using var response = await machine.PostAsync("/fhir/Observation",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/fhir+json"));
        return await FhirAsync<Observation>(response, HttpStatusCode.Created);
    }

    private static string SnapshotUrl(Guid id, DateTimeOffset at) =>
        $"/api/medical-records/{id}/snapshot?at={Uri.EscapeDataString(at.ToString("O", CultureInfo.InvariantCulture))}";

    private static async Task<MedicalRecordSnapshotDto> SnapshotAsync(HttpClient client, Guid id, DateTimeOffset at) =>
        (await client.GetFromJsonAsync<MedicalRecordSnapshotDto>(SnapshotUrl(id, at)))!;

    private static async Task<T> FhirAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
        where T : Resource
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        return new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .Deserialize<T>(await response.Content.ReadAsStringAsync());
    }

    private sealed record Scenario(MedicalRecordDto Record, LaboratoryAnalysisRequestDto[] Requests,
        LaboratoryTestDefinitionDto[] Definitions, DateTimeOffset BeforeResult, Guid ServiceAccountId, string ServiceAccess);
}
