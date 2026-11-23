using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Application.LaboratoryTests.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Factories;
using HospitalInformationSystem.Application.MedicalRecords.Services;
using HospitalInformationSystem.Application.MedicalWorkers.Factories;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.MedicalWorkers;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.ServiceAccounts;
using HospitalInformationSystem.Models.ServiceAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Bundle = Hl7.Fhir.Model.Bundle;
using FhirString = Hl7.Fhir.Model.FhirString;
using Observation = Hl7.Fhir.Model.Observation;
using OperationOutcome = Hl7.Fhir.Model.OperationOutcome;
using Patient = Hl7.Fhir.Model.Patient;
using Practitioner = Hl7.Fhir.Model.Practitioner;
using RequestStatus = Hl7.Fhir.Model.RequestStatus;
using Resource = Hl7.Fhir.Model.Resource;
using ServiceRequest = Hl7.Fhir.Model.ServiceRequest;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class ServiceAuthorizationTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    [Fact]
    public async Task RealServiceBearer_ShouldReadResourcesCreateResultAndUseServiceAuthorWithoutCsrf()
    {
        var account = await factory.CreateAccountAsync();
        var pair = await factory.IssueAsync(account.ApiKey);
        var scenario = await SeedScenarioAsync();
        using var client = factory.CreateMachineClient(pair.AccessToken);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/fhir+json");

        using var patientResponse = await client.GetAsync($"/fhir/Patient/{scenario.PatientId:D}");
        (await FhirAsync<Patient>(patientResponse)).Id.Should().Be(scenario.PatientId.ToString("D"));
        using var practitionerResponse = await client.GetAsync($"/fhir/Practitioner/{scenario.DoctorId:D}");
        (await FhirAsync<Practitioner>(practitionerResponse)).Id.Should().Be(scenario.DoctorId.ToString("D"));
        using var requestResponse = await client.GetAsync($"/fhir/ServiceRequest/{scenario.RequestId:D}");
        (await FhirAsync<ServiceRequest>(requestResponse)).Status.Should().Be(RequestStatus.Active);
        var body = new JsonObject
        {
            ["resourceType"] = "Observation", ["status"] = "final",
            ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject
                { ["system"] = "http://loinc.org", ["code"] = scenario.LoincCode }) },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{scenario.PatientId:D}" },
            ["basedOn"] = new JsonArray(new JsonObject { ["reference"] = $"ServiceRequest/{scenario.RequestId:D}" }),
            ["issued"] = scenario.IssuedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["valueString"] = "5.6"
        };

        using var created = await client.PostAsync("/fhir/Observation",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/fhir+json"));

        var result = await FhirAsync<Observation>(created, HttpStatusCode.Created);
        result.Value.Should().BeOfType<FhirString>().Which.Value.Should().Be("5.6");
        created.Headers.Contains("Set-Cookie").Should().BeFalse();
        using var readResult = await client.GetAsync($"/fhir/Observation/{result.Id}");
        (await FhirAsync<Observation>(readResult)).Id.Should().Be(result.Id);
        using var completed = await client.GetAsync($"/fhir/ServiceRequest/{scenario.RequestId:D}");
        (await FhirAsync<ServiceRequest>(completed)).Status.Should().Be(RequestStatus.Completed);
        using var search = await client.GetAsync($"/fhir/Observation?based-on=ServiceRequest/{scenario.RequestId:D}");
        (await FhirAsync<Bundle>(search)).Entry.Should().ContainSingle().Which.Resource!.Id.Should().Be(result.Id);
        using var repeated = await client.PostAsync("/fhir/Observation",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/fhir+json"));
        await FhirAsync<OperationOutcome>(repeated, HttpStatusCode.Conflict);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var stored = await db.LaboratoryAnalysisResults.AsNoTracking().SingleAsync(row => row.LaboratoryAnalysisRequestId == scenario.RequestId);
        stored.CreatedByServiceAccountId.Should().Be(account.Account.Id);
        var audit = await db.AuditEvents.AsNoTracking().Where(row => row.ActorServiceAccountId == account.Account.Id).ToArrayAsync();
        audit.Should().NotBeEmpty().And.OnlyContain(row => row.ActorUserId == null && row.ActorKind == "service");
        audit.Should().Contain(row => row.MetadataJson.Contains(scenario.RecordId.ToString("D"), StringComparison.Ordinal));
        foreach (var row in audit)
            (row.Message + row.MetadataJson).Should().NotContain(pair.AccessToken).And.NotContain(pair.RefreshToken)
                .And.NotContain(account.ApiKey);
    }

    [Fact]
    public async Task ServiceJwt_ShouldNeverAuthenticateAgainstHumanApiEvenWhenHumanCsrfIsPresent()
    {
        var account = await factory.CreateAccountAsync();
        var pair = await factory.IssueAsync(account.ApiKey);
        using var client = factory.CreateMachineClient(pair.AccessToken);
        foreach (var route in new[] { "/api/auth/session", "/api/accounts", "/api/patients", "/api/service-accounts", "/api/audit" })
        {
            using var response = await client.GetAsync(route);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, route);
        }
        using var created = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest("forbidden-service"));
        created.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var admin = await factory.AdministratorAsync();
        var csrf = (await admin.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
        using var withHumanCsrf = await client.PostAsJsonAsync("/api/service-accounts", new CreateServiceAccountRequest("still-forbidden"));
        withHumanCsrf.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HumanJwt_ShouldUseTheSharedProbeAndKeepFhirAndLoginCsrfProtection()
    {
        using var admin = await factory.AdministratorAsync();
        (await admin.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var probe = await admin.GetAsync("/api/testConnection");
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        (await probe.Content.ReadFromJsonAsync<HumanConnectionStatusDto>())!.Role.Should().Be(ApplicationRoles.Administrator);
        using var fhir = await admin.GetAsync("/fhir/ServiceRequest");
        await FhirAsync<OperationOutcome>(fhir, HttpStatusCode.Unauthorized);
        using var anonymous = factory.CreateMachineClient();
        using var humanLogin = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("not-authorized", "not-authorized"));
        humanLogin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        humanLogin.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await admin.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    public async Task MissingOrMalformedBearer_ShouldReturnProtocolSpecific401AndKeepMetadataPublic(string? token)
    {
        using var client = factory.CreateMachineClient(token);
        using var probe = await client.GetAsync("/api/testConnection");
        await ServiceAuthenticationApiTests.ErrorAsync(probe, HttpStatusCode.Unauthorized, ServiceAuthErrorCodes.Unauthorized);
        using var fhir = await client.GetAsync("/fhir/ServiceRequest");
        await FhirAsync<OperationOutcome>(fhir, HttpStatusCode.Unauthorized);
        using var metadata = await client.GetAsync("/fhir/metadata");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        metadata.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
    }

    [Theory]
    [InlineData("POST", "/fhir/Patient")]
    [InlineData("POST", "/fhir/Practitioner")]
    [InlineData("POST", "/fhir/ServiceRequest")]
    [InlineData("PUT", "/fhir/Observation/55555555-5555-4555-8555-555555555555")]
    [InlineData("DELETE", "/fhir/Observation/55555555-5555-4555-8555-555555555555")]
    public async Task ServiceBearer_ShouldNotEnableUnsupportedMedicalMutations(string method, string path)
    {
        var account = await factory.CreateAccountAsync();
        var pair = await factory.IssueAsync(account.ApiKey);
        using var client = factory.CreateMachineClient(pair.AccessToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
            { Content = new StringContent("{}", Encoding.UTF8, "application/fhir+json") };
        using var response = await client.SendAsync(request);
        await FhirAsync<OperationOutcome>(response, HttpStatusCode.MethodNotAllowed);
    }

    private async Task<ClinicalScenario> SeedScenarioAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var doctor = MedicalWorkerFactory.Create("Проверочный", "Врач", "Врач-терапевт", now, MedicalWorkerRole.Doctor);
        var patient = new PatientDbo
        {
            Id = Guid.NewGuid(), FirstName = "Учебный", LastName = "Пациент", BirthDate = new DateOnly(1985, 3, 12),
            Sex = 0, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var definition = LaboratoryTestDefinitionFactory.Create(
            NewCode(),
            "Исследование реального JWT", now);
        db.MedicalWorkers.Add(new MedicalWorkerDbo
        {
            Id = doctor.Id, FirstName = doctor.FirstName, LastName = doctor.LastName, Position = doctor.Position,
            Role = doctor.Role, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        db.Patients.Add(patient);
        db.LaboratoryTestDefinitions.Add(new LaboratoryTestDefinitionDbo
        {
            Id = definition.Id, LoincCode = definition.LoincCode, DisplayName = definition.DisplayName,
            IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        var record = MedicalRecordFactory.Create(patient.Id, doctor, now);
        var request = MedicalRecordService.CreateLaboratoryAnalysisRequests(record, [definition],
            "Проверка подключения LIS", doctor, now).Single();
        scope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>().Add(record);
        await db.SaveChangesAsync();
        return new ClinicalScenario(patient.Id, doctor.Id, record.Id, request.Id, definition.LoincCode, now);
    }

    private static async Task<T> FhirAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
        where T : Resource
    {
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, json);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict)).Deserialize<T>(json);
    }

    private static string NewCode()
    {
        var number = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray())
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var index = number.Length - 1; index >= 0; index--)
        {
            var digit = number[index] - '0';
            if ((number.Length - index) % 2 == 1)
                digit *= 2;
            sum += digit / 10 + digit % 10;
        }
        return number + "-" + (10 - sum % 10) % 10;
    }

    private sealed record ClinicalScenario(Guid PatientId, Guid DoctorId, Guid RecordId, Guid RequestId,
        string LoincCode, DateTimeOffset IssuedAt);
}
