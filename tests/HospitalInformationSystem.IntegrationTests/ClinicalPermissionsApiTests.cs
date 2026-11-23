using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Patients;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;
using Microsoft.Extensions.DependencyInjection;
using WorkerRole = HospitalInformationSystem.Models.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ClinicalPermissionsApiTests(SecureHospitalApiFactory factory)
    : IClassFixture<SecureHospitalApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateLaboratoryRequests_AnonymousOrAdministrator_ShouldNotGetClinicalAccess(bool administrator)
    {
        using var client = factory.CreateCookieClient();
        await SetCsrfAsync(client);
        if (administrator)
        {
            (await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin())).EnsureSuccessStatusCode();
            await SetCsrfAsync(client);
        }

        var response = await client.PostAsJsonAsync(
            $"/api/medical-records/{Guid.NewGuid()}/laboratory-analysis-requests",
            new { LaboratoryTestDefinitionIds = new[] { Guid.NewGuid() }, Comment = (string?)null });

        response.StatusCode.Should().Be(administrator ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(WorkerRole.Doctor, true, true)]
    [InlineData(WorkerRole.Nurse, true, false)]
    [InlineData(WorkerRole.LaboratoryTechnician, false, false)]
    [InlineData(WorkerRole.Other, false, false)]
    public async Task Send_MedicalRole_ShouldEnforceEveryClinicalMutation(
        WorkerRole role, bool canManagePatients, bool canEditRecord)
    {
        using var client = await CreateClinicianAsync(role);
        (await client.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.OK);
        var missing = Guid.NewGuid();
        (await client.GetAsync($"/api/medical-records/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/medical-records/{missing}/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/medical-records/{missing}/snapshot?at=2026-10-26T12:00:00Z"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var patientRequest = new SavePatientRequest("Проверка", "Роли", null, new DateOnly(1990, 1, 1),
            AdministrativeSex.Male, null);
        var created = await client.PostAsJsonAsync("/api/patients", patientRequest);
        created.StatusCode.Should().Be(canManagePatients ? HttpStatusCode.Created : HttpStatusCode.Forbidden);
        if (canManagePatients)
        {
            var patient = (await created.Content.ReadFromJsonAsync<PatientDto>())!;
            (await client.PostAsync($"/api/patients/{patient.Id}/medical-record", null))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }
        (await client.PutAsJsonAsync($"/api/patients/{missing}", patientRequest)).StatusCode.Should()
            .Be(canManagePatients ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/patients/{missing}")).StatusCode.Should()
            .Be(canManagePatients ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/patients/{missing}/medical-record", null)).StatusCode.Should()
            .Be(canManagePatients ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden);

        var laboratoryTestId = Guid.NewGuid();
        var operations = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, $"/api/medical-records/{missing}/laboratory-analysis-requests",
                new { LaboratoryTestDefinitionIds = new[] { laboratoryTestId }, Comment = (string?)null,
                    ExpectedVersions = new Dictionary<Guid, uint> { [laboratoryTestId] = 1 } }),
            (HttpMethod.Post, $"/api/medical-records/{missing}/diagnoses", new { DiagnosisId = Guid.NewGuid() }),
            (HttpMethod.Delete, $"/api/medical-records/{missing}/diagnoses/{Guid.NewGuid()}", null),
            (HttpMethod.Post, $"/api/medical-records/{missing}/prescriptions", new { Text = "Проверка доступа" }),
            (HttpMethod.Post, $"/api/medical-records/{missing}/prescriptions/{Guid.NewGuid()}/cancel", null),
            (HttpMethod.Put, $"/api/medical-records/{missing}/prescriptions/{Guid.NewGuid()}",
                new { Text = "Проверка доступа" })
        };
        foreach (var operation in operations)
        {
            using var request = new HttpRequestMessage(operation.Method, operation.Url);
            if (operation.Body is not null) request.Content = JsonContent.Create(operation.Body);
            (await client.SendAsync(request)).StatusCode.Should()
                .Be(canEditRecord ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, operation.Url);
        }
        (await client.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> CreateClinicianAsync(WorkerRole role)
    {
        var client = factory.CreateCookieClient();
        const string password = "Role.Matrix-6249!";
        var name = "role." + Guid.NewGuid().ToString("N")[..10];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var worker = new MedicalWorkerDbo
            {
                Id = Guid.NewGuid(), FirstName = "Врач", LastName = "Проверка", Position = "Сотрудник",
                Role = role, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.MedicalWorkers.Add(worker);
            await db.SaveChangesAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
                new CreateAccountRequest(name, password, ApplicationRoles.MedicalWorker, worker.Id),
                CancellationToken.None);
        }
        await SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, password))).EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        return client;
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
