using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Api.Hl7;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.Hl7Import;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Models.ServiceAccounts;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerRole = HospitalInformationSystem.Models.MedicalWorkers.MedicalWorkerRole;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class Lab4Hl7ImportAuditTests(ServiceAuthenticationApiFactory factory)
    : IClassFixture<ServiceAuthenticationApiFactory>
{
    private const string FixtureCredential = "lab4-fixture-access-token-not-valid";
    private const string HiddenException = "Never.Log.L6.Handler.Credential.937";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [Fact]
    public async Task SuccessAndDuplicate_ShouldEachWriteOneImportEventWithActualServiceAndCertificate()
    {
        var account = await CreateServiceAsync("HL7Import");
        var tokens = await factory.IssueAsync(account.ApiKey);
        var certificate = Certificate();
        var externalId = Guid.NewGuid().ToString("N");
        var firstId = ControlId();
        var payload = Payload("adt-no-diagnoses.hl7", tokens.AccessToken, firstId, externalId);

        var first = await ProcessAsync(payload, certificate);

        first.Code.Should().Be("AA");
        Guid.TryParseExact(first.Text, "D", out var patientId).Should().BeTrue();
        patientId.Should().NotBeEmpty();
        var success = await OnlyImportAuditAsync(firstId);
        AssertServiceActor(success, account);
        AssertMetadata(success, Hl7ImportAuditOutcomes.Success, certificate, externalId);
        Metadata(success)["targetId"].GetGuid().Should().Be(patientId);
        Metadata(success)["diagnosisCount"].GetInt32().Should().Be(0);
        AssertNoSecrets(success, tokens.AccessToken, tokens.RefreshToken, account.ApiKey);
        var before = await ClinicalSnapshotAsync();
        var secondId = ControlId();

        var retry = await ProcessAsync(Payload("adt-no-diagnoses.hl7", tokens.AccessToken,
            secondId, externalId), certificate);

        retry.Code.Should().Be("AE");
        retry.Text.Should().StartWith("DUPLICATE:");
        var duplicate = await OnlyImportAuditAsync(secondId);
        AssertServiceActor(duplicate, account);
        AssertMetadata(duplicate, Hl7ImportAuditOutcomes.Duplicate, certificate, externalId);
        Metadata(duplicate)["targetId"].GetGuid().Should().Be(patientId);
        Metadata(duplicate)["diagnosisCount"].GetInt32().Should().Be(0);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("adt-invalid-birth-date.hl7")]
    [InlineData("adt-invalid-second-diagnosis.hl7")]
    public async Task EarlyClinicalRejection_ShouldPersistOneServiceAuditWithoutClinicalWrites(string fixture)
    {
        var account = await CreateServiceAsync("HL7Import");
        var tokens = await factory.IssueAsync(account.ApiKey);
        var certificate = Certificate();
        var controlId = ControlId();
        var externalId = Guid.NewGuid().ToString("N");
        var before = await ClinicalSnapshotAsync();

        var response = await ProcessAsync(Payload(fixture, tokens.AccessToken, controlId, externalId), certificate);

        response.Code.Should().Be("AE");
        response.CorrelationId.Should().Be(controlId);
        response.Text.Should().StartWith("VALIDATION:");
        var entry = await OnlyImportAuditAsync(controlId);
        AssertServiceActor(entry, account);
        AssertMetadata(entry, Hl7ImportAuditOutcomes.ValidationFailed, certificate, externalId);
        Metadata(entry).ContainsKey("targetId").Should().BeFalse();
        Metadata(entry)["diagnosisCount"].GetInt32().Should().Be(0);
        AssertNoSecrets(entry, tokens.AccessToken, tokens.RefreshToken, account.ApiKey);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("lis")]
    [InlineData("revoked")]
    [InlineData("malformed-uac")]
    public async Task CredentialRejection_ShouldAuditAnonymousVerifiedCertificateWithoutClaimedPatientOrService(string kind)
    {
        var token = "invalid-access-token";
        ServiceAccountKeyResultDto? account = null;
        string? refreshToken = null;
        if (kind is "lis" or "revoked")
        {
            account = await CreateServiceAsync(kind == "lis" ? "LIS" : "HL7Import");
            var pair = await factory.IssueAsync(account.ApiKey);
            token = pair.AccessToken;
            refreshToken = pair.RefreshToken;
            if (kind == "revoked") await RevokeAsync(account.Account.Id);
        }
        var certificate = Certificate();
        var controlId = ControlId();
        var externalId = Guid.NewGuid().ToString("N");
        var before = await ClinicalSnapshotAsync();
        var fixture = kind == "malformed-uac" ? "adt-invalid-uac-base64.hl7" : "adt-multiple-diagnoses.hl7";

        var response = await ProcessAsync(Payload(fixture, token, controlId, externalId), certificate);

        response.Code.Should().Be("AR");
        response.Text.Should().StartWith("AUTH:");
        response.Raw.Should().Contain("ERR|||207^Application internal error^HL70357|E|||");
        var entry = await OnlyImportAuditAsync(controlId);
        entry.ActorKind.Should().Be("anonymous");
        entry.ActorServiceAccountId.Should().BeNull();
        entry.ActorUserId.Should().BeNull();
        var metadata = AssertMetadata(entry, Hl7ImportAuditOutcomes.AuthenticationRejected, certificate);
        metadata.ContainsKey("serviceAccountId").Should().BeFalse();
        metadata.ContainsKey("externalPatientId").Should().BeFalse();
        metadata.ContainsKey("targetId").Should().BeFalse();
        entry.MetadataJson.Should().NotContain(externalId).And.NotContain("Кузнецов").And.NotContain("Дмитрий");
        AssertNoSecrets(entry, token, refreshToken ?? "", account?.ApiKey ?? "", "%%%NOT-BASE64%%%");
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task InvalidCredentialWithInvalidClinicalData_ShouldAuditOnlyAuthenticationRefusal()
    {
        const string token = "invalid-before-clinical-decoding";
        var controlId = ControlId();
        var externalId = Guid.NewGuid().ToString("N");
        var certificate = Certificate();
        var before = await ClinicalSnapshotAsync();

        var response = await ProcessAsync(Payload("adt-invalid-birth-date.hl7", token,
            controlId, externalId), certificate);

        response.Code.Should().Be("AR");
        var entry = await OnlyImportAuditAsync(controlId);
        entry.ActorKind.Should().Be("anonymous");
        AssertMetadata(entry, Hl7ImportAuditOutcomes.AuthenticationRejected, certificate);
        Metadata(entry).ContainsKey("externalPatientId").Should().BeFalse();
        AssertNoSecrets(entry, token, externalId);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("message-type")]
    [InlineData("version")]
    public async Task ProtocolRejectionBeforeTokenValidation_ShouldWriteCorrelatedAnonymousEvent(string problem)
    {
        const string token = "unvalidated-protocol-credential";
        var controlId = ControlId();
        var certificate = Certificate();
        var payload = Payload("adt-no-diagnoses.hl7", token, controlId, Guid.NewGuid().ToString("N"));
        payload = ChangeField(payload, "MSH", problem == "version" ? 12 : 9,
            problem == "version" ? "2.7" : "ORM^O01^ORM_O01");
        var before = await ClinicalSnapshotAsync();

        var response = await ProcessAsync(payload, certificate);

        response.Code.Should().Be("AR");
        response.CorrelationId.Should().Be(controlId);
        response.Text.Should().StartWith("PROTOCOL:");
        var entry = await OnlyImportAuditAsync(controlId);
        entry.ActorKind.Should().Be("anonymous");
        entry.ActorServiceAccountId.Should().BeNull();
        AssertMetadata(entry, Hl7ImportAuditOutcomes.ProtocolRejected, certificate);
        Metadata(entry).ContainsKey("externalPatientId").Should().BeFalse();
        AssertNoSecrets(entry, token);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task UnreadableHeader_ShouldNotCreateAnUncorrelatedImportEvent()
    {
        var before = await ImportAuditCountAsync();
        await using var scope = factory.Services.CreateAsyncScope();

        var response = await scope.ServiceProvider.GetRequiredService<Hl7MessageProcessor>()
            .ProcessAsync(Utf8.GetBytes("not-an-HL7-header\r"), Certificate(), "192.0.2.61",
                CancellationToken.None);

        response.Should().BeNull();
        (await ImportAuditCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task SuccessfulProbe_ShouldNotCreatePatientOrImportEvent()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var before = await ClinicalSnapshotAsync();
        var eventsBefore = await ImportAuditCountAsync();

        var response = await ProcessAsync(Payload("probe.hl7", pair.AccessToken, ControlId()), Certificate());

        response.Code.Should().Be("AA");
        response.Text.Should().Be("CONNECTED");
        (await ClinicalSnapshotAsync()).Should().Be(before);
        (await ImportAuditCountAsync()).Should().Be(eventsBefore);
    }

    [Fact]
    public async Task L3ClinicalRejection_ShouldKeepItsSinglePostRollbackEvent()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var controlId = ControlId();
        var externalId = Guid.NewGuid().ToString("N");
        var certificate = Certificate();
        var payload = ChangeField(Payload("adt-multiple-diagnoses.hl7", pair.AccessToken,
            controlId, externalId), "DG1", 5, "20990101010101+0300");
        var before = await ClinicalSnapshotAsync();

        var response = await ProcessAsync(payload, certificate);

        response.Code.Should().Be("AE");
        response.Text.Should().StartWith("VALIDATION:");
        var entry = await OnlyImportAuditAsync(controlId);
        AssertServiceActor(entry, account);
        AssertMetadata(entry, Hl7ImportAuditOutcomes.ValidationFailed, certificate, externalId);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task HandlerFailureAfterRollback_ShouldAuditInSeparateScopeWithoutPersistingPendingPatient()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var controlId = ControlId();
        var externalId = Guid.NewGuid().ToString("N");
        var certificate = Certificate();
        var before = await ClinicalSnapshotAsync();
        await using var observed = factory.Application.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRequestHandler<ImportPatientCommand, ImportPatientResult>>();
                services.AddScoped<IRequestHandler<ImportPatientCommand, ImportPatientResult>>(provider =>
                    new PendingPatientFailure(provider.GetRequiredService<HospitalDbContext>(),
                        provider.GetRequiredService<TimeProvider>()));
            }));

        var response = await ProcessAsync(Payload("adt-no-diagnoses.hl7", pair.AccessToken,
            controlId, externalId), certificate, observed.Services);

        response.Code.Should().Be("AE");
        response.Text.Should().StartWith("INTERNAL:");
        var entry = await OnlyImportAuditAsync(controlId);
        AssertServiceActor(entry, account);
        AssertMetadata(entry, Hl7ImportAuditOutcomes.InternalError, certificate, externalId);
        AssertNoSecrets(entry, HiddenException, pair.AccessToken, pair.RefreshToken, account.ApiKey);
        response.Raw.Should().NotContain(HiddenException);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task CertificateClaimInBody_ShouldBeRejectedAndNeverReplaceTransportCertificate()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var certificate = Certificate();
        var controlId = ControlId();
        const string forgedSubject = "CN=forged-certificate-from-PID";
        var payload = ChangeField(Payload("adt-no-diagnoses.hl7", pair.AccessToken,
            controlId, Guid.NewGuid().ToString("N")), "PID", 11, forgedSubject);
        var before = await ClinicalSnapshotAsync();

        var response = await ProcessAsync(payload, certificate);

        response.Code.Should().Be("AR");
        response.Text.Should().StartWith("PROTOCOL:");
        var entry = await OnlyImportAuditAsync(controlId);
        AssertServiceActor(entry, account);
        AssertMetadata(entry, Hl7ImportAuditOutcomes.ProtocolRejected, certificate);
        AssertNoSecrets(entry, forgedSubject, pair.AccessToken, account.ApiKey);
        (await ClinicalSnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task AdministratorAuditApi_ShouldExposeImportDetailsWithTypeTimeAndStablePageFilters()
    {
        var account = await CreateServiceAsync("HL7Import");
        var pair = await factory.IssueAsync(account.ApiKey);
        var externalId = Guid.NewGuid().ToString("N");
        var certificate = Certificate();
        var ids = new[] { ControlId(), ControlId() };
        await ProcessAsync(Payload("adt-no-diagnoses.hl7", pair.AccessToken, ids[0], externalId), certificate);
        await ProcessAsync(Payload("adt-no-diagnoses.hl7", pair.AccessToken, ids[1], externalId), certificate);
        var stored = new[] { await OnlyImportAuditAsync(ids[0]), await OnlyImportAuditAsync(ids[1]) };
        stored[0].EventTimeUtc.Should().Be(stored[1].EventTimeUtc);
        var at = stored[0].EventTimeUtc;
        using var administrator = await factory.AdministratorAsync();

        var first = await ReadAuditPageAsync(administrator, at, at, page: 1, pageSize: 1);
        var second = await ReadAuditPageAsync(administrator, at.ToOffset(TimeSpan.FromHours(3)), at,
            page: 2, pageSize: 1);

        first.HasMore.Should().BeTrue();
        second.HasMore.Should().BeFalse();
        var expectedIds = stored.Select(row => row.EventId).OrderDescending().ToArray();
        first.Items.Should().ContainSingle().Which.EventId.Should().Be(expectedIds[0]);
        second.Items.Should().ContainSingle().Which.EventId.Should().Be(expectedIds[1]);
        foreach (var item in first.Items.Concat(second.Items))
        {
            item.EventType.Should().Be(AuditEventType.Hl7PatientImported);
            item.Actor.Kind.Should().Be(AuditActorKind.ServiceAccount);
            item.Actor.ServiceAccountId.Should().Be(account.Account.Id);
            item.Actor.DisplayName.Should().Be(account.Account.Name);
            item.Actor.RequestId.Should().BeOneOf(ids);
            item.Metadata["externalPatientId"].GetString().Should().Be(externalId);
            item.Metadata["clientCertificateSha256"].GetString().Should().Be(certificate.Sha256Fingerprint);
            item.Metadata["clientCertificateSerialNumber"].GetString().Should().Be(certificate.SerialNumber);
            item.Metadata.ContainsKey("targetId").Should().BeTrue();
            var serialized = JsonSerializer.Serialize(item);
            serialized.Should().NotContain(pair.AccessToken).And.NotContain(pair.RefreshToken)
                .And.NotContain(account.ApiKey).And.NotContain("UAC|");
        }
        (await ReadAuditPageAsync(administrator, at.AddSeconds(1), at.AddSeconds(2), page: 1, pageSize: 10))
            .Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("medical-worker", HttpStatusCode.Forbidden)]
    [InlineData("LIS", HttpStatusCode.Unauthorized)]
    [InlineData("HL7Import", HttpStatusCode.Unauthorized)]
    public async Task AuditApi_ShouldDenyNonAdministratorIdentities(string kind, HttpStatusCode expected)
    {
        var token = kind == "medical-worker"
            ? await HumanTokenAsync()
            : (await factory.IssueAsync((await CreateServiceAsync(kind)).ApiKey)).AccessToken;
        using var client = factory.CreateMachineClient(token);

        using var response = await client.GetAsync("/api/audit?eventType=Hl7PatientImported");

        response.StatusCode.Should().Be(expected);
    }

    private async Task<ServiceAccountKeyResultDto> CreateServiceAsync(string role)
    {
        using var administrator = await factory.AdministratorAsync();
        using var response = await administrator.PostAsJsonAsync("/api/service-accounts",
            new CreateServiceAccountRequest("l6-audit-" + Guid.NewGuid().ToString("N"), role));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceAccountKeyResultDto>())!;
    }

    private async Task RevokeAsync(Guid accountId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        (await scope.ServiceProvider.GetRequiredService<IServiceSessionRevoker>().RevokeAllAsync(
            accountId, now, "lab4-audit-integration", CancellationToken.None)).Should().Be(1);
        await transaction.CommitAsync();
    }

    private async Task<string> HumanTokenAsync()
    {
        var name = "l6." + Guid.NewGuid().ToString("N")[..12];
        const string password = "Lab4.Audit-Only.6249!";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            var worker = new MedicalWorkerDbo
            {
                Id = Guid.NewGuid(), FirstName = "Проверка", LastName = "Аудита", Position = "Сотрудник",
                Role = WorkerRole.Doctor, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.MedicalWorkers.Add(worker);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
                new CreateAccountRequest(name, password, ApplicationRoles.MedicalWorker, worker.Id),
                CancellationToken.None);
        }
        using var browser = factory.Application.CreateDefaultClient(new Uri("https://localhost"), new JwtBrowserTestHandler());
        await ServiceAccountsApiTests.SetCsrfAsync(browser);
        using var response = await browser.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthenticationTokensDto>())!.AccessToken;
    }

    private ImportClientCertificate Certificate()
    {
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new ImportClientCertificate(new string('C', 64), "CN=L6 verified transport peer",
            "CN=L6 synthetic CA", "A6B7C8D9", now.AddDays(-1), now.AddDays(10));
    }

    private async Task<Acknowledgement> ProcessAsync(byte[] payload, ImportClientCertificate certificate,
        IServiceProvider? services = null)
    {
        await using var scope = (services ?? factory.Services).CreateAsyncScope();
        var bytes = await scope.ServiceProvider.GetRequiredService<Hl7MessageProcessor>()
            .ProcessAsync(payload, certificate, "192.0.2.61", CancellationToken.None);
        bytes.Should().NotBeNull();
        var raw = Utf8.GetString(bytes!);
        var segments = raw.Split('\r', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Split('|')).ToArray();
        var msa = segments.Should().ContainSingle(segment => segment[0] == "MSA").Subject;
        return new Acknowledgement(msa[1], msa[2], msa[3], raw);
    }

    private async Task<AuditEventDbo> OnlyImportAuditAsync(string controlId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents.AsNoTracking()
            .Where(row => row.EventType == nameof(AuditEventType.Hl7PatientImported) && row.RequestId == controlId)
            .ToArrayAsync();
        return events.Should().ContainSingle().Subject;
    }

    private async Task<int> ImportAuditCountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().AuditEvents.AsNoTracking()
            .CountAsync(row => row.EventType == nameof(AuditEventType.Hl7PatientImported));
    }

    private static Dictionary<string, JsonElement> Metadata(AuditEventDbo entry) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entry.MetadataJson)!;

    private static Dictionary<string, JsonElement> AssertMetadata(AuditEventDbo entry, string outcome,
        ImportClientCertificate certificate, string? externalId = null)
    {
        var metadata = Metadata(entry);
        metadata["action"].GetString().Should().Be("hl7-patient-import");
        metadata["outcome"].GetString().Should().Be(outcome);
        metadata["correlationId"].GetString().Should().Be(entry.RequestId);
        metadata["sourceSystem"].GetString().Should().Be("LAB4_XML");
        metadata["clientCertificateSha256"].GetString().Should().Be(certificate.Sha256Fingerprint);
        metadata["clientCertificateSubject"].GetString().Should().Be(certificate.Subject);
        metadata["clientCertificateIssuer"].GetString().Should().Be(certificate.Issuer);
        metadata["clientCertificateSerialNumber"].GetString().Should().Be(certificate.SerialNumber);
        metadata["clientCertificateNotBeforeUtc"].GetDateTimeOffset().Should().Be(certificate.NotBeforeUtc.ToUniversalTime());
        metadata["clientCertificateNotAfterUtc"].GetDateTimeOffset().Should().Be(certificate.NotAfterUtc.ToUniversalTime());
        if (externalId is not null) metadata["externalPatientId"].GetString().Should().Be(externalId);
        return metadata;
    }

    private static void AssertServiceActor(AuditEventDbo entry, ServiceAccountKeyResultDto account)
    {
        entry.ActorKind.Should().Be("service");
        entry.ActorServiceAccountId.Should().Be(account.Account.Id);
        entry.ActorUserId.Should().BeNull();
        entry.ActorDisplayName.Should().Be(account.Account.Name);
        Metadata(entry)["serviceAccountId"].GetGuid().Should().Be(account.Account.Id);
    }

    private static void AssertNoSecrets(AuditEventDbo entry, params string[] credentials)
    {
        var content = entry.Message + entry.MetadataJson;
        content.Should().NotContain("UAC|").And.NotContain("PID|").And.NotContain("MSH|");
        foreach (var credential in credentials.Where(value => value.Length > 0))
        {
            content.Should().NotContain(credential);
            content.Should().NotContain(Convert.ToBase64String(Utf8.GetBytes(credential)));
        }
    }

    private async Task<PagedResult<AuditEventDto>> ReadAuditPageAsync(HttpClient client, DateTimeOffset from,
        DateTimeOffset to, int page, int pageSize)
    {
        var url = "/api/audit?eventType=Hl7PatientImported&page=" + page.ToString(CultureInfo.InvariantCulture) +
                  "&pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture) +
                  "&from=" + Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture)) +
                  "&to=" + Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture));
        using var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResult<AuditEventDto>>())!;
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

    private static string ControlId() => "l6-" + Guid.NewGuid().ToString("N");

    private static byte[] Payload(string fixture, string token, string controlId, string? externalId = null)
    {
        var text = Utf8.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lab4", fixture)));
        text = text.Replace(Convert.ToBase64String(Utf8.GetBytes(FixtureCredential)),
            Convert.ToBase64String(Utf8.GetBytes(token)), StringComparison.Ordinal);
        var segments = text[..^1].Split('\r');
        var msh = segments[0].Split('|');
        msh[9] = controlId;
        segments[0] = string.Join('|', msh);
        var index = Array.FindIndex(segments, segment => segment.StartsWith("PID|", StringComparison.Ordinal));
        if (externalId is not null && index >= 0)
        {
            var pid = segments[index].Split('|');
            pid[3] = externalId + "^^^LAB4_XML^PI";
            segments[index] = string.Join('|', pid);
        }
        return Utf8.GetBytes(string.Join('\r', segments) + '\r');
    }

    private static byte[] ChangeField(byte[] payload, string segmentName, int field, string value)
    {
        var segments = Utf8.GetString(payload)[..^1].Split('\r');
        var index = Array.FindIndex(segments, segment => segment.StartsWith(segmentName + "|", StringComparison.Ordinal));
        var values = segments[index].Split('|').ToList();
        var position = segmentName == "MSH" ? field - 1 : field;
        while (values.Count <= position) values.Add("");
        values[position] = value;
        segments[index] = string.Join('|', values);
        return Utf8.GetBytes(string.Join('\r', segments) + '\r');
    }

    private sealed record Acknowledgement(string Code, string CorrelationId, string Text, string Raw);
    private sealed record ClinicalSnapshot(string Patients, string ExternalIdentifiers, string Records,
        string Diagnoses, string Definitions, string History);

    private sealed class PendingPatientFailure(HospitalDbContext db, TimeProvider clock)
        : IRequestHandler<ImportPatientCommand, ImportPatientResult>
    {
        public async Task<ImportPatientResult> Handle(ImportPatientCommand request, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var input = request.Patient;
            var now = clock.GetUtcNow();
            db.Patients.Add(new PatientDbo
            {
                Id = Guid.NewGuid(), FirstName = input.FirstName, LastName = input.LastName,
                MiddleName = input.MiddleName, BirthDate = input.BirthDate, Sex = (int)input.Sex,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            throw new InvalidOperationException(HiddenException);
        }
    }
}
