using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Application.Models.Auth;
using HospitalInformationSystem.Application.Models.Diagnoses;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using HospitalInformationSystem.Application.Models.MedicalWorkers;
using HospitalInformationSystem.Application.Models.Patients;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MedicalWorkerRole = HospitalInformationSystem.Application.Models.Security.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class BusinessAuditApiTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task MutateRecord_AllClinicalActions_ShouldAuditEachActionWithoutCopyingClinicalPayload()
    {
        using var administrator = await AdministratorAsync();
        var account = await CreateAccountAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(account.Account.UserName, account.GeneratedPassword));
        var privateText = "PRIVATE_" + Guid.NewGuid().ToString("N")[..16];
        var patientResponse = await clinician.PostAsJsonAsync("/api/patients", new SavePatientRequest(
            "Пациент", "Проверка", null, new DateOnly(2000, 1, 1), AdministrativeSex.Male, privateText));
        patientResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            await patientResponse.Content.ReadAsStringAsync());
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDto>())!;
        var recordResponse = await clinician.PostAsync($"/api/patients/{patient.Id}/medical-record", null);
        recordResponse.EnsureSuccessStatusCode();
        var record = (await recordResponse.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
        var definitionResponse = await administrator.PostAsJsonAsync("/api/diagnoses",
            new SaveDiagnosisRequest(Guid.NewGuid().ToString("N")[..6], "Проверка диагноза"));
        definitionResponse.EnsureSuccessStatusCode();
        var definition = (await definitionResponse.Content.ReadFromJsonAsync<DiagnosisDefinitionDto>())!;
        var diagnosisResponse = await clinician.PostAsJsonAsync($"/api/medical-records/{record.Id}/diagnoses",
            new AddMedicalRecordDiagnosisRequest(definition.Id));
        diagnosisResponse.EnsureSuccessStatusCode();
        var diagnosis = (await diagnosisResponse.Content.ReadFromJsonAsync<MedicalRecordDiagnosisDto>())!;
        (await clinician.DeleteAsync($"/api/medical-records/{record.Id}/diagnoses/{diagnosis.Id}")).EnsureSuccessStatusCode();
        var prescriptionResponse = await clinician.PostAsJsonAsync($"/api/medical-records/{record.Id}/prescriptions",
            new SavePrescriptionRequest(privateText));
        prescriptionResponse.EnsureSuccessStatusCode();
        var prescription = (await prescriptionResponse.Content.ReadFromJsonAsync<PrescriptionDto>())!;
        var replacedResponse = await clinician.PutAsJsonAsync($"/api/medical-records/{record.Id}/prescriptions/{prescription.Id}",
            new SavePrescriptionRequest(privateText + " заменено"));
        replacedResponse.EnsureSuccessStatusCode();
        var replacement = (await replacedResponse.Content.ReadFromJsonAsync<PrescriptionDto>())!;
        (await clinician.PostAsync($"/api/medical-records/{record.Id}/prescriptions/{replacement.Id}/cancel", null))
            .EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var events = (await db.AuditEvents.AsNoTracking().Where(entry =>
                entry.EventType == nameof(AuditEventType.MedicalRecordCreated) ||
                entry.EventType == nameof(AuditEventType.MedicalRecordChanged)).ToListAsync(CancellationToken.None))
            .Where(entry => Metadata(entry.MetadataJson).GetProperty("targetId").GetGuid() == record.Id).ToArray();
        events.Should().HaveCount(7);
        events.Select(entry => Metadata(entry.MetadataJson).GetProperty("action").GetString()).Should().BeEquivalentTo(
            new[] { "MedicalRecordCreated", "DiagnosisAdded", "DiagnosisRemoved", "PrescriptionAdded",
                "PrescriptionCancelled", "PrescriptionAdded", "PrescriptionCancelled" });
        events.Should().OnlyContain(entry => entry.ActorKind == "user" && entry.ActorUserId == account.Account.Id &&
            !entry.MetadataJson.Contains(privateText) && !entry.Message.Contains(privateText));
        events.Should().OnlyContain(entry => Metadata(entry.MetadataJson).GetProperty("medicalWorkerId").GetGuid() ==
            account.Account.MedicalWorkerId);
        (await db.MedicalRecordHistoryEvents.Where(entry => entry.MedicalRecordId == record.Id)
            .Select(entry => entry.PayloadJson).ToListAsync(CancellationToken.None))
            .Should().Contain(payload => payload.Contains(privateText));
    }

    [Fact]
    public async Task DeleteWorker_LinkedAccount_ShouldKeepAdministratorActorAndCorrelationAcrossRevocation()
    {
        using var administrator = await AdministratorAsync();
        var account = await CreateAccountAsync(administrator);
        using var clinician = factory.CreateCookieClient();
        await LoginAsync(clinician, new LoginRequest(account.Account.UserName, account.GeneratedPassword));
        var administratorSession = (await administrator.GetFromJsonAsync<SessionDto>("/api/auth/session"))!;

        (await administrator.DeleteAsync($"/api/medical-workers/{account.Account.MedicalWorkerId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var events = await db.AuditEvents.AsNoTracking().Where(entry =>
            entry.EventType == nameof(AuditEventType.MedicalWorkerDeleted) ||
            entry.EventType == nameof(AuditEventType.AccountChanged)).ToListAsync(CancellationToken.None);
        var deleted = events.Single(entry => entry.EventType == nameof(AuditEventType.MedicalWorkerDeleted) &&
            Metadata(entry.MetadataJson).GetProperty("targetId").GetGuid() == account.Account.MedicalWorkerId);
        var revoked = events.Single(entry => entry.EventType == nameof(AuditEventType.AccountChanged) &&
            Metadata(entry.MetadataJson).GetProperty("accountId").GetGuid() == account.Account.Id);
        deleted.ActorUserId.Should().Be(administratorSession.Id);
        revoked.ActorUserId.Should().Be(administratorSession.Id).And.NotBe(account.Account.Id);
        deleted.RequestId.Should().NotBeNullOrWhiteSpace().And.Be(revoked.RequestId);
        Metadata(deleted.MetadataJson).GetProperty("correlationId").GetString().Should()
            .Be(Metadata(revoked.MetadataJson).GetProperty("correlationId").GetString());
        Metadata(revoked.MetadataJson).GetProperty("revokedRefreshTokenCount").GetInt32().Should().Be(1);
        (await clinician.GetAsync("/api/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateAccount_AuditDatabaseFailure_ShouldRollbackIdentityRoleAndWorkerBinding()
    {
        using var administrator = await AdministratorAsync();
        var worker = await CreateWorkerAsync(administrator);
        var userName = "rollback" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_test_audit_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Synthetic audit storage failure'; END $$;
            CREATE TRIGGER reject_test_audit BEFORE INSERT ON security_audit_events
                FOR EACH ROW EXECUTE FUNCTION reject_test_audit_insert();
            """, CancellationToken.None);
        try
        {
            var response = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
                new CreateWorkerAccountRequest(userName));

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_test_audit ON security_audit_events;
                DROP FUNCTION reject_test_audit_insert();
                """, CancellationToken.None);
        }
        (await db.Users.AnyAsync(user => user.UserName == userName || user.MedicalWorkerId == worker.Id,
            CancellationToken.None)).Should().BeFalse();
        (await db.MedicalWorkers.AnyAsync(item => item.Id == worker.Id && !item.IsDeleted, CancellationToken.None))
            .Should().BeTrue();
    }

    private async Task<HttpClient> AdministratorAsync()
    {
        var client = factory.CreateCookieClient();
        await LoginAsync(client, factory.AdministratorLogin());
        return client;
    }

    private static async Task<MedicalWorkerDto> CreateWorkerAsync(HttpClient administrator)
    {
        var response = await administrator.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Врач", "Проверочный", "Врач", MedicalWorkerRole.Doctor));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MedicalWorkerDto>())!;
    }

    private static async Task<CreateWorkerAccountResultDto> CreateAccountAsync(HttpClient administrator)
    {
        var worker = await CreateWorkerAsync(administrator);
        var response = await administrator.PostAsJsonAsync($"/api/medical-workers/{worker.Id}/account",
            new CreateWorkerAccountRequest("audit" + Guid.NewGuid().ToString("N")[..8]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateWorkerAccountResultDto>())!;
    }

    private static JsonElement Metadata(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static async Task LoginAsync(HttpClient client, LoginRequest credentials)
    {
        await SetCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", credentials)).EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
    }
}
