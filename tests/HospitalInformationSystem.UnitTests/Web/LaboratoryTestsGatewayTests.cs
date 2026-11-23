using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HospitalInformationSystem.Models.Auth;
using HospitalInformationSystem.Models.LaboratoryTests;
using HospitalInformationSystem.Models.Security;
using HospitalInformationSystem.Web.ApiClients;
using HospitalInformationSystem.Web.Authentication;
using HospitalInformationSystem.Web.Gateways;

namespace HospitalInformationSystem.UnitTests.Web;

public sealed class LaboratoryTestsGatewayTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(ApplicationRoles.MedicalWorker, HttpStatusCode.Forbidden)]
    [InlineData("Unknown", HttpStatusCode.Forbidden)]
    public async Task Non_administrators_cannot_write_even_outside_authorized_page(string? role, HttpStatusCode status)
    {
        var requests = 0;
        using var http = Client(_ =>
        {
            requests++;
            throw new InvalidOperationException("Unauthorized write must not reach HTTP.");
        });
        var gateway = new LaboratoryTestsGateway(new LaboratoryTestsApiClient(http), Session(role));

        Func<Task> create = () => gateway.SaveAsync(null, new SaveLaboratoryTestRequest("2345-7", "Глюкоза"), CancellationToken.None);
        Func<Task> update = () => gateway.SaveAsync(Guid.NewGuid(), new SaveLaboratoryTestRequest("2345-7", "Глюкоза"), CancellationToken.None);
        Func<Task> activate = () => gateway.SetActiveAsync(Guid.NewGuid(), true, 17, CancellationToken.None);

        (await create.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(status);
        (await update.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(status);
        (await activate.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(status);
        requests.Should().Be(0);
    }

    [Fact]
    public async Task Administrators_create_update_and_change_status_using_the_shared_client()
    {
        var test = new LaboratoryTestDefinitionDto(Guid.NewGuid(), "2345-7", "Глюкоза", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 17);
        var paths = new List<string>();
        using var http = Client(request =>
        {
            paths.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(test) };
        });
        var gateway = new LaboratoryTestsGateway(new LaboratoryTestsApiClient(http), Session(ApplicationRoles.Administrator));
        var request = new SaveLaboratoryTestRequest(test.LoincCode, test.DisplayName);

        await gateway.SaveAsync(null, request, CancellationToken.None);
        await gateway.SaveAsync(test.Id, request with { Version = test.Version }, CancellationToken.None);
        await gateway.SetActiveAsync(test.Id, false, test.Version, CancellationToken.None);

        paths.Should().Equal("POST /api/laboratory-tests", $"PUT /api/laboratory-tests/{test.Id}",
            $"PUT /api/laboratory-tests/{test.Id}/active");
    }

    [Fact]
    public async Task Medical_workers_can_read_active_catalog_for_clinical_selection()
    {
        string? query = null;
        using var http = Client(request =>
        {
            query = request.RequestUri!.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<LaboratoryTestDefinitionDto>())
            };
        });
        var gateway = new LaboratoryTestsGateway(new LaboratoryTestsApiClient(http), Session(ApplicationRoles.MedicalWorker));

        await gateway.ListAsync(CancellationToken.None);

        query.Should().Be("?activeOnly=true");
    }

    private static SessionState Session(string? role)
    {
        var session = new SessionState();
        if (role is not null)
            session.SetSession(new SessionDto(Guid.NewGuid(), "user", role,
                role == ApplicationRoles.MedicalWorker ? Guid.NewGuid() : null, MedicalWorkerRole.Doctor));
        return session;
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new ResponseHandler(send)) { BaseAddress = new Uri("https://localhost/") };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
