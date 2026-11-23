using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Models.Fhir;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DomainPatient = HospitalInformationSystem.Models.Patients.Patient;
using DomainStatus = HospitalInformationSystem.Models.MedicalRecords.LaboratoryAnalysisRequestStatus;
using DomainWorker = HospitalInformationSystem.Models.MedicalWorkers.MedicalWorker;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirProtocolApiTests(FhirReadApiFactory factory) : IClassFixture<FhirReadApiFactory>
{
    [Theory]
    [InlineData("application/fhir+json;q=0, */*;q=1")]
    [InlineData("application/*;q=0, */*;q=1")]
    [InlineData("application/fhir+json;fhirVersion=4.0")]
    public async System.Threading.Tasks.Task Negotiation_ShouldRespectRejectedMediaAndUnsupportedFhirVersions(string accept)
    {
        using var client = factory.CreateServiceClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);

        using var response = await client.GetAsync("/fhir/metadata");

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
    }

    [Fact]
    public async System.Threading.Tasks.Task UnexpectedException_ShouldReturnSafeOutcomeWithoutDatabaseOrSecretDetails()
    {
        await using var observed = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFhirReadRepository>();
            services.AddScoped<IFhirReadRepository>(_ => new ThrowingRepository());
        }));
        using var client = observed.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://his-api:5201"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("FhirTest", "lis");

        using var response = await client.GetAsync($"/fhir/Patient/{factory.PatientId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("SELECT").And.NotContain("DO-NOT-LEAK").And.NotContain("InvalidOperationException");
        var outcome = Parse<OperationOutcome>(json);
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Exception);
        outcome.Issue.Single().Details!.Coding.Single().Code.Should().Be("internal_error");
    }

    [Fact]
    public async System.Threading.Tasks.Task RequestBodyLimit_ShouldReturnFhirOutcomeFromExistingMiddleware()
    {
        using var client = factory.CreateServiceClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/metadata")
            { Content = new ByteArrayContent(new byte[128 * 1024]) };
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        var outcome = Parse<OperationOutcome>(await response.Content.ReadAsStringAsync());
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.TooCostly);
    }

    [Fact]
    public async System.Threading.Tasks.Task UnsupportedMethod_ShouldReturnAllowAndFhirOutcomeWithoutChangingData()
    {
        using var client = factory.CreateServiceClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/fhir/Patient/{factory.PatientId:D}");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        response.Content.Headers.Allow.Should().Contain("GET");
        var outcome = Parse<OperationOutcome>(await response.Content.ReadAsStringAsync());
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.NotSupported);
    }

    private static T Parse<T>(string json) where T : Resource =>
        new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .Deserialize<T>(json);

    private sealed class ThrowingRepository : IFhirReadRepository
    {
        private static Exception Failure() => new InvalidOperationException("SELECT DO-NOT-LEAK-PRIVATE-KEY FROM patient");
        public Task<DomainPatient?> GetPatientAsync(Guid id, CancellationToken ct) => throw Failure();
        public Task<DomainWorker?> GetPractitionerAsync(Guid id, CancellationToken ct) => throw Failure();
        public Task<FhirServiceRequestReadModel?> GetServiceRequestAsync(Guid id, CancellationToken ct) => throw Failure();
        public Task<FhirObservationReadModel?> GetObservationAsync(Guid id, CancellationToken ct) => throw Failure();
        public Task<FhirReadPage<FhirServiceRequestReadModel>> SearchServiceRequestsAsync(DomainStatus? status, int count,
            FhirPagePosition? after, CancellationToken ct) => throw Failure();
        public Task<FhirReadPage<FhirObservationReadModel>> SearchObservationsAsync(Guid basedOn, int count,
            FhirPagePosition? after, CancellationToken ct) => throw Failure();
    }
}
