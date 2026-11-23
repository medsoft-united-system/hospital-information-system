using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Gateways;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryAnalysisRequestsClientTests
{
    private static readonly Guid PatientId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    [Fact]
    public async Task Create_ShouldSendSelectionCommentAndReadVersionsAndReadTheEntireCreatedBatch()
    {
        var recordId = Guid.NewGuid();
        var definitions = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var versions = new Dictionary<Guid, uint> { [definitions[0]] = 17, [definitions[1]] = 29 };
        var created = definitions.Select(id => Definition(recordId, id)).Reverse().ToArray();
        var sends = 0;
        using var http = Client(async (message, cancellationToken) =>
        {
            sends++;
            message.Method.Should().Be(HttpMethod.Post);
            message.RequestUri!.AbsolutePath.Should().Be($"/api/medical-records/{recordId}/laboratory-analysis-requests");
            message.Headers.Contains("X-Medical-Worker-Id").Should().BeFalse();
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellationToken));
            body.RootElement.EnumerateObject().Select(x => x.Name).Should()
                .BeEquivalentTo(["laboratoryTestDefinitionIds", "comment", "expectedVersions"]);
            body.RootElement.GetProperty("laboratoryTestDefinitionIds").EnumerateArray()
                .Select(x => x.GetGuid()).Should().Equal(definitions);
            body.RootElement.GetProperty("comment").GetString().Should().Be("Контрольный анализ");
            body.RootElement.GetProperty("expectedVersions").EnumerateObject()
                .ToDictionary(x => Guid.Parse(x.Name), x => x.Value.GetUInt32()).Should().BeEquivalentTo(versions);
            return Created(created);
        });

        var response = await new MedicalRecordsApiClient(http).CreateLaboratoryAnalysisRequestsAsync(recordId,
            new CreateLaboratoryAnalysisRequestsRequest(definitions, "Контрольный анализ", versions), CancellationToken.None);

        response.Should().Equal(created);
        sends.Should().Be(1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("{\"items\":[]}")]
    [InlineData("invalid json")]
    [InlineData("[]")]
    public async Task Create_MalformedSuccessBody_ShouldNotReportSuccessfulAssignment(string json)
    {
        var sends = 0;
        using var http = Client((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var action = () => new MedicalRecordsApiClient(http).CreateLaboratoryAnalysisRequestsAsync(Guid.NewGuid(),
            new CreateLaboratoryAnalysisRequestsRequest([Guid.NewGuid()], null), CancellationToken.None);

        await action.Should().ThrowAsync<ApiException>().WithMessage("*некорректный ответ*");
        sends.Should().Be(1);
    }

    [Theory]
    [InlineData("wrong-record")]
    [InlineData("missing-request")]
    [InlineData("duplicate-request-id")]
    [InlineData("unexpected-definition")]
    [InlineData("completed-request")]
    [InlineData("mixed-patient")]
    [InlineData("mixed-requester")]
    [InlineData("wrong-status-code")]
    public async Task Create_InconsistentBatchResponse_ShouldRequireCheckingServerState(string problem)
    {
        var recordId = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var created = ids.Select(id => Definition(recordId, id)).ToArray();
        switch (problem)
        {
            case "wrong-record": created[0] = created[0] with { MedicalRecordId = Guid.NewGuid() }; break;
            case "missing-request": created = [created[0]]; break;
            case "duplicate-request-id": created[1] = created[1] with { Id = created[0].Id }; break;
            case "unexpected-definition": created[0] = created[0] with { LaboratoryTestDefinitionId = Guid.NewGuid() }; break;
            case "completed-request": created[0] = created[0] with { Status = LaboratoryAnalysisRequestStatus.Completed }; break;
            case "mixed-patient": created[0] = created[0] with { PatientId = Guid.NewGuid() }; break;
            case "mixed-requester": created[0] = created[0] with { RequestedByMedicalWorkerId = Guid.NewGuid() }; break;
        }
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(
            problem == "wrong-status-code" ? HttpStatusCode.OK : HttpStatusCode.Created)
        {
            Content = JsonContent.Create(created)
        }));

        var action = () => new MedicalRecordsApiClient(http).CreateLaboratoryAnalysisRequestsAsync(recordId,
            new CreateLaboratoryAnalysisRequestsRequest(ids, null), CancellationToken.None);

        await action.Should().ThrowAsync<ApiException>().WithMessage("*проверить результат назначения*");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Create_ServerRejectsBatch_ShouldPreserveFailureAndNeverRetry(HttpStatusCode status)
    {
        var sends = 0;
        using var http = Client((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new { detail = "Справочник изменился. Обновите выбранные анализы." })
            });
        });

        var action = () => new MedicalRecordsApiClient(http).CreateLaboratoryAnalysisRequestsAsync(Guid.NewGuid(),
            new CreateLaboratoryAnalysisRequestsRequest([Guid.NewGuid()], null), CancellationToken.None);

        var error = (await action.Should().ThrowAsync<ApiException>()).Which;
        error.StatusCode.Should().Be(status);
        error.Message.Should().Contain("Справочник изменился");
        sends.Should().Be(1);
    }

    [Fact]
    public async Task Create_Cancellation_ShouldCancelTheOutstandingHttpOperationWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        using var http = Client(async (_, token) =>
        {
            sends++;
            token.CanBeCanceled.Should().BeTrue();
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancelled request must not complete.");
        });
        var action = new MedicalRecordsApiClient(http).CreateLaboratoryAnalysisRequestsAsync(Guid.NewGuid(),
            new CreateLaboratoryAnalysisRequestsRequest([Guid.NewGuid()], null), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Func<Task> complete = async () => await action;
        await complete.Should().ThrowAsync<OperationCanceledException>();
        sends.Should().Be(1);
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData(ApplicationRoles.Administrator, MedicalWorkerRole.Doctor, HttpStatusCode.Forbidden)]
    [InlineData(ApplicationRoles.MedicalWorker, MedicalWorkerRole.Nurse, HttpStatusCode.Forbidden)]
    [InlineData(ApplicationRoles.MedicalWorker, MedicalWorkerRole.LaboratoryTechnician, HttpStatusCode.Forbidden)]
    [InlineData(ApplicationRoles.MedicalWorker, MedicalWorkerRole.Other, HttpStatusCode.Forbidden)]
    [InlineData(ApplicationRoles.MedicalWorker, null, HttpStatusCode.Forbidden)]
    [InlineData(ApplicationRoles.MedicalWorker, (MedicalWorkerRole)999, HttpStatusCode.Forbidden)]
    [InlineData("Unknown", MedicalWorkerRole.Doctor, HttpStatusCode.Forbidden)]
    public async Task Gateway_NonDoctor_ShouldRejectBeforeSending(string? accountRole, MedicalWorkerRole? medicalRole,
        HttpStatusCode status)
    {
        var sends = 0;
        using var http = Client((_, _) =>
        {
            sends++;
            throw new InvalidOperationException("Forbidden assignment must not reach HTTP.");
        });
        var session = Session(accountRole, medicalRole);
        var gateway = new MedicalRecordsGateway(new MedicalRecordsApiClient(http), session);

        var action = () => gateway.CreateLaboratoryAnalysisRequestsAsync(Guid.NewGuid(),
            new CreateLaboratoryAnalysisRequestsRequest([Guid.NewGuid()], null), CancellationToken.None);

        (await action.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(status);
        sends.Should().Be(0);
    }

    [Fact]
    public async Task Gateway_Doctor_ShouldReturnConfirmedServerRequests()
    {
        var recordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var response = Definition(recordId, definitionId);
        using var http = Client((_, _) => Task.FromResult(Created(new[] { response })));
        var gateway = new MedicalRecordsGateway(new MedicalRecordsApiClient(http),
            Session(ApplicationRoles.MedicalWorker, MedicalWorkerRole.Doctor));

        var created = await gateway.CreateLaboratoryAnalysisRequestsAsync(recordId,
            new CreateLaboratoryAnalysisRequestsRequest([definitionId], null), CancellationToken.None);

        created.Should().Equal(response);
    }

    [Fact]
    public async Task Gateway_DoctorWithoutLinkedWorker_ShouldRejectBeforeSending()
    {
        using var http = Client((_, _) => throw new InvalidOperationException("Missing worker must not reach HTTP."));
        var session = new SessionState();
        session.SetSession(new SessionDto(Guid.NewGuid(), "unlinked", ApplicationRoles.MedicalWorker, null,
            MedicalWorkerRole.Doctor));
        var gateway = new MedicalRecordsGateway(new MedicalRecordsApiClient(http), session);

        var action = () => gateway.CreateLaboratoryAnalysisRequestsAsync(Guid.NewGuid(),
            new CreateLaboratoryAnalysisRequestsRequest([Guid.NewGuid()], null), CancellationToken.None);

        (await action.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static LaboratoryAnalysisRequestDto Definition(Guid recordId, Guid definitionId) => new(
        Guid.NewGuid(), recordId, PatientId, WorkerId, definitionId, "2345-7", "Глюкоза", null,
        new DateTimeOffset(2026, 11, 9, 10, 15, 25, TimeSpan.Zero), LaboratoryAnalysisRequestStatus.Active, null,
        "Врач при назначении");

    private static SessionState Session(string? accountRole, MedicalWorkerRole? medicalRole)
    {
        var session = new SessionState();
        if (accountRole is not null)
            session.SetSession(new SessionDto(Guid.NewGuid(), "test", accountRole, Guid.NewGuid(), medicalRole));
        return session;
    }

    private static HttpResponseMessage Created<T>(T value) => new(HttpStatusCode.Created)
        { Content = JsonContent.Create(value) };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new DelegateHandler(send)) { BaseAddress = new Uri("https://localhost/") };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
