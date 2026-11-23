using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Hl7Import;
using HospitalInformationSystem.Models.Patients;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkerRole = HospitalInformationSystem.Models.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab4Hl7MessageProcessingTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    private const string FixtureCredential = "lab4-fixture-access-token-not-valid";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [Fact]
    public async Task AuthorizedAdt_ShouldCommitPatientDiagnosesHistoryAndAuditBeforeAcknowledgingAndRejectRetry()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var externalId = Guid.NewGuid().ToString("N");
        var controlId = "l4-" + Guid.NewGuid().ToString("N");
        var certificate = Certificate();
        var payload = Payload("adt-multiple-diagnoses.hl7", pair.AccessToken, controlId, externalId);

        var acknowledgement = await ProcessAsync(payload, certificate);

        acknowledgement.Code.Should().Be("AA");
        acknowledgement.CorrelationId.Should().Be(controlId);
        acknowledgement.Header[8].Should().Be("ACK^A28^ACK");
        acknowledgement.Header[9].Should().NotBe(controlId);
        acknowledgement.Header[2..6].Should().Equal("HIS", "MEDSOFT", "MEDSOFT_IMPORT", "LAB4_XML");
        Guid.TryParseExact(acknowledgement.Text, "D", out var patientId).Should().BeTrue();
        patientId.Should().NotBeEmpty();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var patient = await db.Patients.AsNoTracking().SingleAsync(row => row.Id == patientId);
            patient.LastName.Should().Be("Кузнецов");
            patient.FirstName.Should().Be("Дмитрий");
            patient.MiddleName.Should().Be("Андреевич");
            patient.BirthDate.Should().Be(new DateOnly(1985, 2, 28));
            var identifier = await db.PatientExternalIdentifiers.AsNoTracking()
                .SingleAsync(row => row.SourceSystem == "LAB4_XML" && row.ExternalPatientId == externalId);
            identifier.PatientId.Should().Be(patientId);
            var record = await db.MedicalRecords.AsNoTracking().SingleAsync(row => row.PatientId == patientId);
            var diagnoses = await db.MedicalRecordDiagnoses.AsNoTracking()
                .Where(row => row.MedicalRecordId == record.Id).OrderBy(row => row.DiagnosisCodeSnapshot).ToArrayAsync();
            diagnoses.Select(row => row.DiagnosisCodeSnapshot).Should().Equal("E78.5", "I10");
            diagnoses.Should().OnlyContain(row => row.AddedByServiceAccountId == account.Account.Id &&
                row.AddedByMedicalWorkerId == null && row.DiagnosedAtOffsetMinutes == 180 &&
                row.CodingSystem == "I10" && row.DiagnosisType == "U");
            diagnoses[0].DiagnosedAtUtc.Should().Be(new DateTimeOffset(2024, 3, 22, 11, 30, 0, TimeSpan.Zero));
            diagnoses[1].DiagnosedAtUtc.Should().Be(new DateTimeOffset(2022, 1, 10, 6, 15, 0, TimeSpan.Zero));
            var codes = diagnoses.Select(row => row.DiagnosisCodeSnapshot).ToArray();
            (await db.DiagnosisDefinitions.CountAsync(row => codes.Contains(row.Code))).Should().Be(2);
            var history = await db.MedicalRecordHistoryEvents.AsNoTracking()
                .Where(row => row.MedicalRecordId == record.Id).OrderBy(row => row.Version).ToArrayAsync();
            history.Select(row => row.EventType).Should().Equal("MedicalRecordCreated", "DiagnosisAdded", "DiagnosisAdded");
            history.Should().OnlyContain(row => row.PerformedByServiceAccountId == account.Account.Id &&
                row.PerformedByMedicalWorkerId == null && row.PerformedByNameSnapshot == account.Account.Name);
            var audit = (await ImportAuditsAsync(account.Account.Id)).Should().ContainSingle().Subject;
            audit.RequestId.Should().Be(controlId);
            audit.ActorKind.Should().Be("service");
            audit.ActorDisplayName.Should().Be(account.Account.Name);
            using var metadata = JsonDocument.Parse(audit.MetadataJson);
            var json = metadata.RootElement;
            json.GetProperty("outcome").GetString().Should().Be("success");
            json.GetProperty("externalPatientId").GetString().Should().Be(externalId);
            json.GetProperty("diagnosisCount").GetInt32().Should().Be(2);
            json.GetProperty("targetId").GetGuid().Should().Be(patientId);
            json.GetProperty("clientCertificateSha256").GetString().Should().Be(certificate.Sha256Fingerprint);
            json.GetProperty("clientCertificateSubject").GetString().Should().Be(certificate.Subject);
            json.GetProperty("clientCertificateIssuer").GetString().Should().Be(certificate.Issuer);
            json.GetProperty("clientCertificateSerialNumber").GetString().Should().Be(certificate.SerialNumber);
        }
        var beforeRetry = await ClinicalSnapshotAsync();
        var retryId = "l4-retry-" + Guid.NewGuid().ToString("N");
        var retry = Payload("adt-multiple-diagnoses.hl7", pair.AccessToken, retryId, externalId);

        var duplicate = await ProcessAsync(retry, certificate);

        duplicate.Code.Should().Be("AE");
        duplicate.CorrelationId.Should().Be(retryId);
        duplicate.Text.Should().Be("DUPLICATE: пациент уже импортирован");
        (await ClinicalSnapshotAsync()).Should().Be(beforeRetry);
        var audits = await ImportAuditsAsync(account.Account.Id);
        audits.Should().HaveCount(2);
        audits.Select(audit => JsonDocument.Parse(audit.MetadataJson))
            .Select(document =>
            {
                using (document) return document.RootElement.GetProperty("outcome").GetString();
            }).Should().BeEquivalentTo("success", "duplicate");
    }

    [Theory]
    [InlineData(25)]
    [InlineData(64)]
    public async Task AuthorizedAdt_WithLongPatientNames_ShouldCommitCompleteValuesAndReturnThemThroughApi(int length)
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var externalId = Guid.NewGuid().ToString("N");
        var controlId = "issue40-" + Guid.NewGuid().ToString("N");
        var names = new[] { new string('Ф', length), new string('И', length), new string('О', length) };
        var payload = WithPatientNames(Payload("adt-no-diagnoses.hl7", pair.AccessToken, controlId, externalId), names);

        var acknowledgement = await ProcessAsync(payload, Certificate());

        acknowledgement.Code.Should().Be("AA");
        acknowledgement.CorrelationId.Should().Be(controlId);
        Guid.TryParseExact(acknowledgement.Text, "D", out var patientId).Should().BeTrue();
        patientId.Should().NotBeEmpty();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var patient = await db.Patients.AsNoTracking().SingleAsync(row => row.Id == patientId);
            new[] { patient.LastName, patient.FirstName, patient.MiddleName }.Should().Equal(names);
            var identifier = await db.PatientExternalIdentifiers.AsNoTracking()
                .SingleAsync(row => row.SourceSystem == "LAB4_XML" && row.ExternalPatientId == externalId);
            identifier.PatientId.Should().Be(patientId);
            (await db.MedicalRecords.CountAsync(row => row.PatientId == patientId)).Should().Be(1);
        }

        using var reader = factory.Application.CreateDefaultClient(new Uri("https://localhost"));
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await HumanTokenAsync());
        using var response = await reader.GetAsync("/api/patients/" + patientId.ToString("D"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var readback = (await response.Content.ReadFromJsonAsync<PatientDto>())!;
        new[] { readback.LastName, readback.FirstName, readback.MiddleName }.Should().Equal(names);
        readback.HasMedicalRecord.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AuthorizedAdt_WithOversizedPatientName_ShouldRejectWithoutClinicalWritesAndAuditOnce(int component)
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var externalId = Guid.NewGuid().ToString("N");
        var controlId = "issue40-rejected-" + Guid.NewGuid().ToString("N");
        var names = new[] { "Иванов", "Алексей", "Петрович" };
        names[component] = new string('Я', 65);
        var payload = WithPatientNames(Payload("adt-no-diagnoses.hl7", pair.AccessToken, controlId, externalId), names);
        var before = await ClinicalSnapshotAsync();

        var acknowledgement = await ProcessAsync(payload, Certificate());

        acknowledgement.Code.Should().Be("AE");
        acknowledgement.CorrelationId.Should().Be(controlId);
        acknowledgement.Text.Should().StartWith("VALIDATION:");
        (await ClinicalSnapshotAsync()).Should().Be(before);
        var audit = (await ImportAuditsAsync(account.Account.Id)).Should().ContainSingle().Subject;
        audit.RequestId.Should().Be(controlId);
        using var metadata = JsonDocument.Parse(audit.MetadataJson);
        metadata.RootElement.GetProperty("outcome").GetString().Should().Be("validation-failed");
        metadata.RootElement.GetProperty("externalPatientId").GetString().Should().Be(externalId);
        metadata.RootElement.TryGetProperty("targetId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizedProbe_ShouldReturnConnectedWithoutClinicalWritesOrImportAudit()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var controlId = "l4-probe-" + Guid.NewGuid().ToString("N");
        var before = await ClinicalSnapshotAsync();

        var acknowledgement = await ProcessAsync(Payload("probe.hl7", pair.AccessToken, controlId), Certificate());

        acknowledgement.Code.Should().Be("AA");
        acknowledgement.CorrelationId.Should().Be(controlId);
        acknowledgement.Text.Should().Be("CONNECTED");
        acknowledgement.Header[8].Should().Be("ACK^N02^ACK");
        (await ClinicalSnapshotAsync()).Should().Be(before);
        (await ImportAuditsAsync(account.Account.Id)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("lis")]
    [InlineData("human")]
    [InlineData("invalid")]
    [InlineData("revoked")]
    public async Task UnauthorizedCredential_ShouldReturnAuthErrorAndLeaveClinicalStateUntouched(string kind)
    {
        var accessToken = kind == "human" ? await HumanTokenAsync() : "invalid-access-token";
        if (kind is "lis" or "revoked")
        {
            var account = await CreateServiceAsync(kind == "lis" ? "LIS" : "HL7Import");
            accessToken = (await factory.IssueAsync(account.ApiKey)).AccessToken;
            if (kind == "revoked")
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
                (await scope.ServiceProvider.GetRequiredService<IServiceSessionRevoker>().RevokeAllAsync(
                    account.Account.Id, now, "lab4-codec-integration", CancellationToken.None)).Should().Be(1);
                await transaction.CommitAsync();
            }
        }
        var controlId = "l4-refused-" + Guid.NewGuid().ToString("N");
        var before = await ClinicalSnapshotAsync();
        var auditsBefore = await ImportAuditCountAsync();
        var payload = Payload("adt-multiple-diagnoses.hl7", accessToken, controlId, Guid.NewGuid().ToString("N"));

        var acknowledgement = await ProcessAsync(payload, Certificate());

        AssertAuthenticationFailure(acknowledgement, controlId, accessToken);
        (await ClinicalSnapshotAsync()).Should().Be(before);
        (await ImportAuditCountAsync()).Should().Be(auditsBefore + 1);
    }

    [Theory]
    [InlineData("adt-invalid-birth-date.hl7")]
    [InlineData("adt-invalid-second-diagnosis.hl7")]
    public async Task InvalidClinicalFields_ShouldReturnValidationWithoutPartialClinicalWrites(string fixture)
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var controlId = "l4-validation-" + Guid.NewGuid().ToString("N");
        var before = await ClinicalSnapshotAsync();
        var payload = Payload(fixture, pair.AccessToken, controlId, Guid.NewGuid().ToString("N"));

        var acknowledgement = await ProcessAsync(payload, Certificate());

        acknowledgement.Code.Should().Be("AE");
        acknowledgement.CorrelationId.Should().Be(controlId);
        acknowledgement.Text.Should().StartWith("VALIDATION:");
        acknowledgement.Raw.Should().NotContain(pair.AccessToken);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task InvalidCredential_ShouldBeRefusedBeforeDecodingInvalidClinicalFields()
    {
        const string invalidToken = "invalid-access-token";
        var controlId = "l4-auth-first-" + Guid.NewGuid().ToString("N");
        var before = await ClinicalSnapshotAsync();
        var auditsBefore = await ImportAuditCountAsync();
        var payload = Payload("adt-invalid-birth-date.hl7", invalidToken, controlId, Guid.NewGuid().ToString("N"));

        var acknowledgement = await ProcessAsync(payload, Certificate());

        AssertAuthenticationFailure(acknowledgement, controlId, invalidToken);
        (await ClinicalSnapshotAsync()).Should().Be(before);
        (await ImportAuditCountAsync()).Should().Be(auditsBefore + 1);
    }

    private async Task<ServiceAccountKeyResultDto> CreateServiceAsync(string role)
    {
        using var administrator = await factory.AdministratorAsync();
        using var response = await administrator.PostAsJsonAsync("/api/service-accounts",
            new CreateServiceAccountRequest("l4-message-" + Guid.NewGuid().ToString("N"), role));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>())!;
    }

    private async Task<string> HumanTokenAsync()
    {
        var userName = "l4." + Guid.NewGuid().ToString("N")[..12];
        const string password = "Lab4.Message-Processor.6249!";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            var worker = new MedicalWorkerDbo
            {
                Id = Guid.NewGuid(), FirstName = "Проверка", LastName = "Сообщений", Position = "Сотрудник",
                Role = WorkerRole.Doctor, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.MedicalWorkers.Add(worker);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
                new CreateAccountRequest(userName, password, ApplicationRoles.MedicalWorker, worker.Id), CancellationToken.None);
        }
        using var browser = factory.Application.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
        await ServiceAccountsApiTests.SetCsrfAsync(browser);
        using var response = await browser.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!.AccessToken;
    }

    private ImportClientCertificate Certificate()
    {
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new ImportClientCertificate(new string('B', 64), "CN=L4 integration desktop", "CN=L4 synthetic CA",
            "ABCDEF012345", now.AddDays(-1), now.AddDays(10));
    }

    private async Task<Acknowledgement> ProcessAsync(byte[] payload, ImportClientCertificate certificate)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var response = await scope.ServiceProvider.GetRequiredService<Hl7MessageProcessor>()
            .ProcessAsync(payload, certificate, "192.0.2.42", CancellationToken.None);
        response.Should().NotBeNull();
        var text = Utf8.GetString(response!);
        text.Should().StartWith("MSH|").And.EndWith("\r").And.NotContain("\n");
        var segments = text[..^1].Split('\r').Select(segment => segment.Split('|')).ToArray();
        var msa = segments.Should().ContainSingle(segment => segment[0] == "MSA").Subject;
        segments[0][0].Should().Be("MSH");
        return new Acknowledgement(msa[1], msa[2], msa[3], segments[0],
            segments.Where(segment => segment[0] == "ERR").ToArray(), text);
    }

    private static byte[] Payload(string fixture, string accessToken, string controlId, string? externalId = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", fixture);
        var text = Utf8.GetString(File.ReadAllBytes(path));
        text = text.Replace(Convert.ToBase64String(Utf8.GetBytes(FixtureCredential)),
            Convert.ToBase64String(Utf8.GetBytes(accessToken)), StringComparison.Ordinal);
        var segments = text[..^1].Split('\r');
        var msh = segments[0].Split('|');
        msh[9] = controlId;
        segments[0] = string.Join('|', msh);
        var pidIndex = Array.FindIndex(segments, segment => segment.StartsWith("PID|", StringComparison.Ordinal));
        if (externalId is not null && pidIndex >= 0)
        {
            var pid = segments[pidIndex].Split('|');
            pid[3] = externalId + "^^^LAB4_XML^PI";
            segments[pidIndex] = string.Join('|', pid);
        }
        return Utf8.GetBytes(string.Join('\r', segments) + '\r');
    }

    private static byte[] WithPatientNames(byte[] payload, string[] names)
    {
        var segments = Utf8.GetString(payload).Split('\r');
        var index = Array.FindIndex(segments, segment => segment.StartsWith("PID|", StringComparison.Ordinal));
        var fields = segments[index].Split('|');
        fields[5] = string.Join('^', names);
        segments[index] = string.Join('|', fields);
        return Utf8.GetBytes(string.Join('\r', segments));
    }

    private static void AssertAuthenticationFailure(Acknowledgement acknowledgement, string controlId, string token)
    {
        acknowledgement.Code.Should().Be("AR");
        acknowledgement.CorrelationId.Should().Be(controlId);
        acknowledgement.Text.Should().Be("AUTH: доступ запрещён");
        acknowledgement.Errors.Should().ContainSingle();
        acknowledgement.Errors[0][3].Should().Be("207^Application internal error^HL70357");
        acknowledgement.Errors[0][4].Should().Be("E");
        acknowledgement.Errors[0][7].Should().Be("Доступ запрещён");
        acknowledgement.Raw.Should().NotContain(token).And.NotContain(Convert.ToBase64String(Utf8.GetBytes(token)));
    }

    private async Task<ClinicalSnapshot> ClinicalSnapshotAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return new ClinicalSnapshot(
            JsonSerializer.Serialize(await db.Patients.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.PatientExternalIdentifiers.AsNoTracking()
                .OrderBy(row => row.SourceSystem).ThenBy(row => row.ExternalPatientId).ToArrayAsync()),
            JsonSerializer.Serialize(await db.MedicalRecords.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.MedicalRecordDiagnoses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.DiagnosisDefinitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()),
            JsonSerializer.Serialize(await db.MedicalRecordHistoryEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    private async Task<AuditEventDbo[]> ImportAuditsAsync(Guid accountId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents.AsNoTracking()
            .Where(row => row.EventType == "Hl7PatientImported" && row.ActorServiceAccountId == accountId).ToArrayAsync();
    }

    private async Task<int> ImportAuditCountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents
            .CountAsync(row => row.EventType == "Hl7PatientImported");
    }

    private sealed record Acknowledgement(string Code, string CorrelationId, string Text, string[] Header,
        string[][] Errors, string Raw);
    private sealed record ClinicalSnapshot(string Patients, string ExternalIdentifiers, string Records,
        string Diagnoses, string Definitions, string History);
}
