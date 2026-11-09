using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Contracts.Auth;
using HospitalInformationSystem.Contracts.MedicalWorkers;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalRecords.Services;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Bundle = Hl7.Fhir.Model.Bundle;
using FhirString = Hl7.Fhir.Model.FhirString;
using Observation = Hl7.Fhir.Model.Observation;
using ObservationStatus = Hl7.Fhir.Model.ObservationStatus;
using OperationOutcome = Hl7.Fhir.Model.OperationOutcome;
using RequestStatus = Hl7.Fhir.Model.RequestStatus;
using Resource = Hl7.Fhir.Model.Resource;
using ServiceRequest = Hl7.Fhir.Model.ServiceRequest;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirObservationCreateApiTests(FhirObservationCreateApiFactory factory)
    : IClassFixture<FhirObservationCreateApiFactory>
{
    [Fact]
    public async Task Create_ShouldUseServerIdAndSnapshotsAndCommitResultHistoryAndServiceAudit()
    {
        var state = await factory.CreateScenarioAsync();
        await using (var update = factory.Services.CreateAsyncScope())
        {
            var db = update.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var definition = await db.LaboratoryTestDefinitions.SingleAsync(x => x.Id == state.DefinitionId);
            definition.DisplayName = "Текущее название, которого не было в назначении";
            definition.IsActive = false;
            await db.SaveChangesAsync();
        }
        var clientId = Guid.NewGuid();
        var body = Body(state);
        body["id"] = clientId.ToString("D");
        body["valueString"] = "  Отрицательно 🧪  ";
        body["issued"] = "2026-11-09T13:04:05.123+03:00";
        using var client = factory.CreateServiceClient();
        client.DefaultRequestHeaders.Host = "attacker.invalid";

        using var response = await PostAsync(client, body);

        var result = await ReadAsync<Observation>(response, HttpStatusCode.Created);
        var resultId = Guid.Parse(result.Id!);
        resultId.Should().NotBe(clientId).And.NotBe(state.RequestId);
        response.Headers.Location!.AbsoluteUri.Should().Be($"{FhirObservationCreateApiFactory.FhirBase}/Observation/{result.Id}");
        result.Status.Should().Be(ObservationStatus.Final);
        result.Code!.Coding.Single().Code.Should().Be(state.LoincCode);
        result.Code.Coding.Single().Display.Should().Be(state.DisplayName);
        result.Subject!.Reference.Should().Be($"Patient/{state.PatientId:D}");
        result.BasedOn.Single().Reference.Should().Be($"ServiceRequest/{state.RequestId:D}");
        result.Value.Should().BeOfType<FhirString>().Which.Value.Should().Be("Отрицательно 🧪");
        var responseJson = await response.Content.ReadAsStringAsync();
        using (var raw = JsonDocument.Parse(responseJson))
            raw.RootElement.GetProperty("issued").GetString().Should().Be("2026-11-09T10:04:05.123Z");
        using var readResult = await client.GetAsync(response.Headers.Location);
        (await readResult.Content.ReadAsStringAsync()).Should().Be(responseJson);
        using var readRequest = await client.GetAsync($"/fhir/ServiceRequest/{state.RequestId:D}");
        (await ReadAsync<ServiceRequest>(readRequest)).Status.Should().Be(RequestStatus.Completed);
        using var lookup = await client.GetAsync($"/fhir/Observation?based-on=ServiceRequest/{state.RequestId:D}");
        (await ReadAsync<Bundle>(lookup)).Entry.Should().ContainSingle().Which.Resource!.Id.Should().Be(result.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbAfter = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var saved = (await scope.ServiceProvider.GetRequiredService<IMedicalRecordRepository>()
            .FindAsync(state.RecordId, false, CancellationToken.None))!;
        saved.Version.Should().Be(state.InitialVersion + 1);
        var savedResult = saved.LaboratoryAnalysisRequests.Single().Result!;
        savedResult.Id.Should().Be(resultId);
        savedResult.CreatedByServiceAccountId.Should().Be(FhirObservationCreateApiFactory.ServiceId);
        savedResult.ReceivedAtUtc.Should().Be(FhirObservationCreateApiFactory.ReceivedAt);
        savedResult.IssuedAtUtc.Should().Be(DateTimeOffset.Parse("2026-11-09T10:04:05.123Z", System.Globalization.CultureInfo.InvariantCulture));
        var history = saved.HistoryEvents.Single(x => x.EventType == MedicalRecordEventType.LaboratoryAnalysisResultRecorded);
        history.AuthorKind.Should().Be(MedicalRecordAuthorKind.ServiceAccount);
        history.PerformedByMedicalWorkerId.Should().BeNull();
        history.PerformedByServiceAccountId.Should().Be(FhirObservationCreateApiFactory.ServiceId);
        history.PerformedByNameSnapshot.Should().Be("lis.read-tests");
        history.OccurredAtUtc.Should().Be(savedResult.ReceivedAtUtc);
        MedicalRecordSnapshotReplay.Build(saved, saved.HistoryEvents, savedResult.ReceivedAtUtc.AddTicks(-1))
            .LaboratoryAnalysisRequests.Single().Result.Should().BeNull();
        MedicalRecordSnapshotReplay.Build(saved, saved.HistoryEvents, savedResult.ReceivedAtUtc)
            .LaboratoryAnalysisRequests.Single().Result!.Value.Should().Be(savedResult.Value);
        var audit = (await ResultAuditsAsync(dbAfter, state.RecordId)).Should().ContainSingle().Subject;
        audit.ActorKind.Should().Be("service");
        audit.ActorUserId.Should().BeNull();
        audit.ActorServiceAccountId.Should().Be(FhirObservationCreateApiFactory.ServiceId);
        audit.ActorDisplayName.Should().Be("lis.read-tests");
        audit.RequestId.Should().NotBeNullOrWhiteSpace();
        audit.Message.Should().NotContain(savedResult.Value);
        audit.MetadataJson.Should().NotContain(savedResult.Value).And.NotContain("FhirTest");
    }

    [Fact]
    public async Task Create_SecondSubmission_ShouldReturnExistingResultConflictAndKeepOriginalValue()
    {
        var state = await factory.CreateScenarioAsync();
        using var client = factory.CreateServiceClient();
        using var firstResponse = await PostAsync(client, Body(state));
        var first = await ReadAsync<Observation>(firstResponse, HttpStatusCode.Created);
        var changed = Body(state);
        changed["valueString"] = "Чужое повторное значение";

        using var secondResponse = await PostAsync(client, changed);

        var outcome = await ReadAsync<OperationOutcome>(secondResponse, HttpStatusCode.Conflict);
        ErrorCode(outcome).Should().Be("result_already_exists");
        using var saved = await client.GetAsync(firstResponse.Headers.Location);
        (await ReadAsync<Observation>(saved)).Value.Should().BeOfType<FhirString>().Which.Value.Should().Be("5.6");
        await AssertSingleResultAsync(state, Guid.Parse(first.Id!));
    }

    [Fact]
    public async Task Create_ConcurrentSubmissions_ShouldCommitExactlyOneResultHistoryAndAudit()
    {
        var state = await factory.CreateScenarioAsync();
        factory.RecordReads.Arm(state.RecordId);
        using var first = factory.CreateServiceClient();
        using var second = factory.CreateServiceClient();

        var responses = await Task.WhenAll(PostAsync(first, Body(state)), PostAsync(second, Body(state)));

        try
        {
            responses.Select(x => x.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            var created = await ReadAsync<Observation>(responses.Single(x => x.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
            var rejected = await ReadAsync<OperationOutcome>(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
            ErrorCode(rejected).Should().BeOneOf("state_conflict", "result_already_exists");
            await AssertSingleResultAsync(state, Guid.Parse(created.Id!));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Create_ConcurrentUnrelatedRecordEdit_ShouldNotOverwriteEitherCommittedChange()
    {
        var state = await factory.CreateScenarioAsync();
        factory.RecordReads.Arm(state.RecordId);
        using var client = factory.CreateServiceClient();
        var post = PostAsync(client, Body(state));
        await using var editing = factory.Services.CreateAsyncScope();
        var repository = editing.ServiceProvider.GetRequiredService<IMedicalRecordRepository>();
        var record = (await repository.FindAsync(state.RecordId, true, CancellationToken.None))!;
        MedicalRecordService.AddPrescription(record, "Параллельный контрольный осмотр", factory.Doctor,
            FhirObservationCreateApiFactory.ReceivedAt);
        await repository.UpdateAsync(record, CancellationToken.None);
        var editSucceeded = true;
        try
        {
            await editing.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync();
        }
        catch (BusinessConflictException)
        {
            editSucceeded = false;
        }

        using var response = await post;

        response.StatusCode.Should().Be(editSucceeded ? HttpStatusCode.Conflict : HttpStatusCode.Created);
        await using var verification = factory.Services.CreateAsyncScope();
        var saved = (await verification.ServiceProvider.GetRequiredService<IMedicalRecordRepository>()
            .FindAsync(state.RecordId, false, CancellationToken.None))!;
        saved.Version.Should().Be(state.InitialVersion + 1);
        saved.Prescriptions.Should().HaveCount(editSucceeded ? 1 : 0);
        if (editSucceeded)
            saved.LaboratoryAnalysisRequests.Single().Result.Should().BeNull();
        else
            saved.LaboratoryAnalysisRequests.Single().Result.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_FailureAfterDatabaseWriteBeforeCommit_ShouldRollBackResultHistoryAndAudit()
    {
        var state = await factory.CreateScenarioAsync();
        var failure = new FailResultWriteInterceptor();
        await using var observed = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<HospitalDbContext>(options => options.AddInterceptors(failure))));
        using var client = FhirObservationCreateApiFactory.CreateServiceClient(observed);

        using var response = await PostAsync(client, Body(state));

        var outcome = await ReadAsync<OperationOutcome>(response, HttpStatusCode.InternalServerError);
        ErrorCode(outcome).Should().Be("internal_error");
        failure.Triggered.Should().BeTrue();
        await AssertUnchangedAsync(state);
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("loinc")]
    [InlineData("absolute-subject")]
    [InlineData("absolute-based-on")]
    [InlineData("wrong-reference-type")]
    [InlineData("empty-based-on")]
    [InlineData("multiple-based-on")]
    [InlineData("non-final")]
    [InlineData("quantity")]
    [InlineData("missing-issued")]
    [InlineData("issued-precision")]
    [InlineData("empty-value")]
    [InlineData("long-value")]
    [InlineData("nul-value")]
    [InlineData("wrong-resource")]
    public async Task Create_InvalidProfileOrClinicalLink_ShouldLeaveAllClinicalStateUnchanged(string invalid)
    {
        var state = await factory.CreateScenarioAsync();
        var body = Body(state);
        switch (invalid)
        {
            case "patient": body["subject"]!["reference"] = $"Patient/{Guid.NewGuid():D}"; break;
            case "loinc": body["code"]!["coding"]![0]!["code"] = "999999-9"; break;
            case "absolute-subject": body["subject"]!["reference"] = $"https://attacker.invalid/fhir/Patient/{state.PatientId:D}"; break;
            case "absolute-based-on": body["basedOn"]![0]!["reference"] = $"https://attacker.invalid/fhir/ServiceRequest/{state.RequestId:D}"; break;
            case "wrong-reference-type": body["basedOn"]![0]!["reference"] = $"Patient/{state.RequestId:D}"; break;
            case "empty-based-on": body["basedOn"] = new JsonArray(); break;
            case "multiple-based-on": body["basedOn"]!.AsArray().Add(new JsonObject { ["reference"] = $"ServiceRequest/{Guid.NewGuid():D}" }); break;
            case "non-final": body["status"] = "preliminary"; break;
            case "quantity": body.Remove("valueString"); body["valueQuantity"] = new JsonObject { ["value"] = 5.6 }; break;
            case "missing-issued": body.Remove("issued"); break;
            case "issued-precision": body["issued"] = "2026-11-09T10:04:05.1234Z"; break;
            case "empty-value": body["valueString"] = " \t "; break;
            case "long-value": body["valueString"] = new string('Я', 2001); break;
            case "nul-value": body["valueString"] = "5\0.6"; break;
            case "wrong-resource": body["resourceType"] = "Patient"; break;
        }
        using var client = factory.CreateServiceClient();

        using var response = await PostAsync(client, body);

        await ReadAsync<OperationOutcome>(response, HttpStatusCode.BadRequest);
        await AssertUnchangedAsync(state);
    }

    [Fact]
    public async Task Create_UnknownOrder_ShouldReturnNotFoundWithoutCreatingAnything()
    {
        var state = await factory.CreateScenarioAsync();
        var body = Body(state);
        body["basedOn"]![0]!["reference"] = $"ServiceRequest/{Guid.NewGuid():D}";
        using var client = factory.CreateServiceClient();

        using var response = await PostAsync(client, body);

        ErrorCode(await ReadAsync<OperationOutcome>(response, HttpStatusCode.NotFound)).Should().Be("not_found");
        await AssertUnchangedAsync(state);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task Create_ShouldRequireAcceptedServiceIdentityBeforeAllowingCredentialFreeCsrfException(string? credential, HttpStatusCode expected)
    {
        var state = await factory.CreateScenarioAsync();
        using var client = factory.CreateServiceClient(credential);

        using var response = await PostAsync(client, Body(state));

        await ReadAsync<OperationOutcome>(response, expected);
        await AssertUnchangedAsync(state);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("application/fhir+json;fhirVersion=4.0")]
    public async Task Create_UnsupportedMediaType_ShouldReturn415WithoutChangingData(string contentType)
    {
        var state = await factory.CreateScenarioAsync();
        using var client = factory.CreateServiceClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/Observation")
            { Content = new StringContent(Body(state).ToJsonString(), Encoding.UTF8) };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        using var response = await client.SendAsync(request);

        ErrorCode(await ReadAsync<OperationOutcome>(response, HttpStatusCode.UnsupportedMediaType)).Should().Be("unsupported_media_type");
        await AssertUnchangedAsync(state);
    }

    [Fact]
    public async Task Create_TooLargeBody_ShouldReturn413WithoutChangingData()
    {
        var state = await factory.CreateScenarioAsync();
        using var client = factory.CreateServiceClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/Observation")
            { Content = new StringContent(new string(' ', 64 * 1024 + 1), Encoding.UTF8, "application/fhir+json") };

        using var response = await client.SendAsync(request);

        ErrorCode(await ReadAsync<OperationOutcome>(response, HttpStatusCode.RequestEntityTooLarge)).Should().Be("request_too_large");
        await AssertUnchangedAsync(state);
    }

    [Fact]
    public async Task Create_WithUnexpectedQuery_ShouldReturn400WithoutChangingData()
    {
        var state = await factory.CreateScenarioAsync();
        using var client = factory.CreateServiceClient();

        using var response = await PostAsync(client, Body(state), "/fhir/Observation?patient=override");

        ErrorCode(await ReadAsync<OperationOutcome>(response, HttpStatusCode.BadRequest)).Should().Be("invalid_search");
        await AssertUnchangedAsync(state);
    }

    [Theory]
    [InlineData("PUT", "/fhir/Observation")]
    [InlineData("POST", "/fhir/Patient/11111111-1111-4111-8111-111111111111")]
    [InlineData("POST", "/fhir/Observation/11111111-1111-4111-8111-111111111111")]
    public async Task UnsupportedMutation_ShouldNotGainTheObservationCreateException(string method, string url)
    {
        var state = await factory.CreateScenarioAsync();
        using var client = factory.CreateServiceClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
            { Content = new StringContent(Body(state).ToJsonString(), Encoding.UTF8, "application/fhir+json") };

        using var response = await client.SendAsync(request);

        await ReadAsync<OperationOutcome>(response, HttpStatusCode.MethodNotAllowed);
        await AssertUnchangedAsync(state);
    }

    private async Task AssertUnchangedAsync(FhirObservationCreateApiFactory.Scenario state)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.MedicalRecords.AsNoTracking().SingleAsync(x => x.Id == state.RecordId)).Version.Should().Be(state.InitialVersion);
        (await db.LaboratoryAnalysisRequests.AsNoTracking().SingleAsync(x => x.Id == state.RequestId)).Status.Should().Be(LaboratoryAnalysisRequestStatus.Active);
        (await db.LaboratoryAnalysisResults.CountAsync(x => x.LaboratoryAnalysisRequestId == state.RequestId)).Should().Be(0);
        (await db.MedicalRecordHistoryEvents.CountAsync(x => x.MedicalRecordId == state.RecordId)).Should().Be(2);
        (await ResultAuditsAsync(db, state.RecordId)).Should().BeEmpty();
    }

    private async Task AssertSingleResultAsync(FhirObservationCreateApiFactory.Scenario state, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.LaboratoryAnalysisResults.Where(x => x.LaboratoryAnalysisRequestId == state.RequestId).Select(x => x.Id).ToArrayAsync())
            .Should().Equal(id);
        (await db.MedicalRecords.AsNoTracking().SingleAsync(x => x.Id == state.RecordId)).Version.Should().Be(state.InitialVersion + 1);
        (await db.LaboratoryAnalysisRequests.AsNoTracking().SingleAsync(x => x.Id == state.RequestId)).Status.Should().Be(LaboratoryAnalysisRequestStatus.Completed);
        (await db.MedicalRecordHistoryEvents.CountAsync(x => x.MedicalRecordId == state.RecordId && x.EventType == "LaboratoryAnalysisResultRecorded")).Should().Be(1);
        (await ResultAuditsAsync(db, state.RecordId)).Should().ContainSingle();
    }

    private static async Task<IReadOnlyList<AuditEventDbo>> ResultAuditsAsync(HospitalDbContext db, Guid recordId)
    {
        var entries = await db.AuditEvents.AsNoTracking().Where(x => x.ActorServiceAccountId == FhirObservationCreateApiFactory.ServiceId).ToListAsync();
        return entries.Where(entry =>
        {
            using var metadata = JsonDocument.Parse(entry.MetadataJson);
            return metadata.RootElement.TryGetProperty("targetId", out var id) && id.GetGuid() == recordId &&
                   metadata.RootElement.TryGetProperty("action", out var action) && action.GetString() == "LaboratoryAnalysisResultRecorded";
        }).ToArray();
    }

    private static JsonObject Body(FhirObservationCreateApiFactory.Scenario state) => new()
    {
        ["resourceType"] = "Observation", ["status"] = "final",
        ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject
            { ["system"] = "http://loinc.org", ["code"] = state.LoincCode, ["display"] = "Не доверять названию из клиента" }) },
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{state.PatientId:D}" },
        ["basedOn"] = new JsonArray(new JsonObject { ["reference"] = $"ServiceRequest/{state.RequestId:D}" }),
        ["issued"] = "2026-11-09T10:04:05.123Z", ["valueString"] = "5.6"
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body, string url = "/fhir/Observation")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/fhir+json") };
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        return await client.SendAsync(request);
    }

    private static string? ErrorCode(OperationOutcome outcome) => outcome.Issue.Single().Details!.Coding.Single().Code;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK) where T : Resource
    {
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, json);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict)).Deserialize<T>(json);
    }

    private sealed class FailResultWriteInterceptor : DbCommandInterceptor
    {
        private int _triggered;
        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if ((command.CommandText.Contains("INSERT INTO laboratory_analysis_results", StringComparison.Ordinal) ||
                 command.CommandText.Contains("INSERT INTO \"laboratory_analysis_results\"", StringComparison.Ordinal)) &&
                Interlocked.Exchange(ref _triggered, 1) == 0)
                throw new InvalidOperationException("Synthetic failure after a laboratory write and before transaction commit.");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class FhirObservationHumanBoundaryTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async Task GenuineHumanJwt_ShouldNotCreateObservationAndHumanWritesShouldStillRequireCsrf()
    {
        using var client = factory.CreateCookieClient();
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
        (await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin())).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var content = new StringContent("{}", Encoding.UTF8, "application/fhir+json");

        using var response = await client.PostAsync("/fhir/Observation", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        var outcome = new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .Deserialize<OperationOutcome>(await response.Content.ReadAsStringAsync());
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Login);
        using var humanWrite = await client.PostAsJsonAsync("/api/medical-workers",
            new SaveMedicalWorkerRequest("Иван", "Проверка", "Врач"));
        humanWrite.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        humanWrite.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
