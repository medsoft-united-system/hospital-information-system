using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirReadApiTests(FhirReadApiFactory factory) : IClassFixture<FhirReadApiFactory>
{
    [Fact]
    public async System.Threading.Tasks.Task ReadResources_ShouldReturnStrictR5WithHistoricalReferencesAndSnapshots()
    {
        using var client = factory.CreateServiceClient();
        using var patientResponse = await client.GetAsync($"/fhir/Patient/{factory.PatientId:D}");
        var patient = await ReadAsync<Patient>(patientResponse);
        patient.Active.Should().BeTrue();
        patient.Name.Single().Family.Should().Be("Петров");
        patient.Name.Single().Given.Should().Equal("Алексей", "Андреевич");
        patient.BirthDate.Should().Be("1985-03-12");
        patient.Gender.Should().Be(AdministrativeGender.Male);
        (await patientResponse.Content.ReadAsStringAsync()).Should().NotContain("PRIVATE-PATIENT-COMMENT");
        using var workerResponse = await client.GetAsync($"/fhir/Practitioner/{factory.PractitionerId:D}");
        var worker = await ReadAsync<Practitioner>(workerResponse);
        worker.Active.Should().BeFalse();
        worker.Name.Single().Family.Should().Be("Иванов");
        worker.Qualification.Single().Code!.Text.Should().Be("Врач-терапевт");

        using var requestResponse = await client.GetAsync($"/fhir/ServiceRequest/{factory.CompletedRequestId:D}");
        var request = await ReadAsync<ServiceRequest>(requestResponse);
        request.Status.Should().Be(RequestStatus.Completed);
        request.Subject!.Reference.Should().Be($"Patient/{factory.PatientId:D}");
        request.Requester!.Reference.Should().Be($"Practitioner/{factory.PractitionerId:D}");
        request.Code!.Concept!.Coding.Single().Code.Should().Be("2345-7");
        request.Code!.Concept!.Coding.Single().Display.Should().NotBe("Название после назначения");
        request.AuthoredOn.Should().MatchRegex(@"Z$");
        request.Note.Single().Text.Should().Be("Комментарий к анализу");
        using var resultResponse = await client.GetAsync($"/fhir/Observation/{factory.ObservationId:D}");
        var result = await ReadAsync<Observation>(resultResponse);
        result.Status.Should().Be(ObservationStatus.Final);
        result.BasedOn.Single().Reference.Should().Be($"ServiceRequest/{request.Id}");
        result.Subject!.Reference.Should().Be(request.Subject!.Reference);
        result.Code!.Coding.Single().Code.Should().Be(request.Code!.Concept!.Coding.Single().Code);
        result.Value.Should().BeOfType<FhirString>().Which.Value.Should().Be("5.6");
        result.Id.Should().NotBe(request.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task SearchRequests_ShouldTraverseEveryPageInStableWireOrderAndIgnoreHostHeader()
    {
        using var client = factory.CreateServiceClient();
        client.DefaultRequestHeaders.Host = "attacker.invalid";
        var ids = new List<string>();
        string? url = "/fhir/ServiceRequest?_count=7";
        var visited = new HashSet<string>();
        while (url is not null)
        {
            visited.Add(url).Should().BeTrue();
            using var response = await client.GetAsync(url);
            var bundle = await ReadAsync<Bundle>(response);
            bundle.Type.Should().Be(Bundle.BundleType.Searchset);
            bundle.Link.Single(x => x.Relation == "self").Url.Should().StartWith(FhirReadApiFactory.FhirBase);
            bundle.Entry.Should().HaveCountLessThanOrEqualTo(7);
            foreach (var entry in bundle.Entry)
            {
                entry.Search!.Mode.Should().Be(Bundle.SearchEntryMode.Match);
                entry.FullUrl.Should().Be($"{FhirReadApiFactory.FhirBase}/ServiceRequest/{entry.Resource!.Id}");
                ids.Add(((ServiceRequest)entry.Resource!).Id!);
            }
            url = bundle.Link.SingleOrDefault(x => x.Relation == "next")?.Url;
            if (url is not null)
            {
                url.Should().StartWith(FhirReadApiFactory.FhirBase + "/ServiceRequest?");
                url.Should().NotContain("attacker.invalid");
                url.Should().Contain("_count=7").And.Contain("_cursor=");
            }
        }
        visited.Should().HaveCount(4);
        ids.Should().HaveCount(25).And.OnlyHaveUniqueItems();
        ids.Should().Equal(ids.Order(StringComparer.Ordinal));
        ids.Should().BeEquivalentTo(factory.RequestIds.Select(x => x.ToString("D")));
    }

    [Fact]
    public async System.Threading.Tasks.Task Search_ShouldFilterStatusAndObservationBasedOnAndReturnEmptySearchset()
    {
        using var client = factory.CreateServiceClient();
        using var closedResponse = await client.GetAsync("/fhir/ServiceRequest?status=completed");
        var closed = await ReadAsync<Bundle>(closedResponse);
        closed.Entry.Should().ContainSingle().Which.Resource!.Id.Should().Be(factory.CompletedRequestId.ToString("D"));
        using var foundResponse = await client.GetAsync($"/fhir/Observation?based-on=ServiceRequest/{factory.CompletedRequestId:D}");
        var found = await ReadAsync<Bundle>(foundResponse);
        found.Entry.Should().ContainSingle().Which.Resource!.Id.Should().Be(factory.ObservationId.ToString("D"));
        using var emptyResponse = await client.GetAsync($"/fhir/Observation?based-on=ServiceRequest/{Guid.NewGuid():D}");
        var empty = await ReadAsync<Bundle>(emptyResponse);
        empty.Entry.Should().BeEmpty();
        empty.Total.Should().Be(0);
        using var raw = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
        raw.RootElement.TryGetProperty("entry", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("/fhir/ServiceRequest?status=unknown")]
    [InlineData("/fhir/ServiceRequest?status=")]
    [InlineData("/fhir/ServiceRequest?status=active&status=completed")]
    [InlineData("/fhir/ServiceRequest?_count=0")]
    [InlineData("/fhir/ServiceRequest?_count=101")]
    [InlineData("/fhir/ServiceRequest?_count=-1")]
    [InlineData("/fhir/ServiceRequest?_count=1.5")]
    [InlineData("/fhir/ServiceRequest?_count=2&_count=2")]
    [InlineData("/fhir/ServiceRequest?patient=arbitrary")]
    [InlineData("/fhir/ServiceRequest?_cursor=invalid")]
    [InlineData("/fhir/Observation")]
    [InlineData("/fhir/Observation?based-on=Patient/11111111-1111-4111-8111-111111111111")]
    [InlineData("/fhir/Observation?based-on=https%3A%2F%2Fattacker.invalid%2FServiceRequest%2F11111111-1111-4111-8111-111111111111")]
    [InlineData("/fhir/Observation?based-on=ServiceRequest/not-a-guid")]
    public async System.Threading.Tasks.Task Search_InvalidQuery_ShouldReturnFhirOperationOutcome(string url)
    {
        using var client = factory.CreateServiceClient();
        using var response = await client.GetAsync(url);
        var outcome = await ReadAsync<OperationOutcome>(response, HttpStatusCode.BadRequest);
        outcome.Issue.Single().Details!.Coding.Single().Code.Should().Be("invalid_search");
    }

    [Theory]
    [InlineData("Patient")]
    [InlineData("Practitioner")]
    [InlineData("ServiceRequest")]
    [InlineData("Observation")]
    public async System.Threading.Tasks.Task Read_InvalidOrMissingResource_ShouldDistinguishBadRequestFromNotFound(string type)
    {
        using var client = factory.CreateServiceClient();
        using var invalid = await client.GetAsync($"/fhir/{type}/not-a-guid");
        await ReadAsync<OperationOutcome>(invalid, HttpStatusCode.BadRequest);
        using var missing = await client.GetAsync($"/fhir/{type}/{Guid.NewGuid():D}");
        var outcome = await ReadAsync<OperationOutcome>(missing, HttpStatusCode.NotFound);
        outcome.Issue.Single().Details!.Coding.Single().Code.Should().Be("not_found");
    }

    [Fact]
    public async System.Threading.Tasks.Task Cursor_ShouldRejectChangesOfFilterCountResourceOrCiphertext()
    {
        using var client = factory.CreateServiceClient();
        using var first = await client.GetAsync("/fhir/ServiceRequest?status=active&_count=2");
        var bundle = await ReadAsync<Bundle>(first);
        var next = bundle.Link.Single(x => x.Relation == "next").Url!;
        foreach (var changed in new[] { next.Replace("status=active", "status=completed"),
                     next.Replace("_count=2", "_count=3"), next.Replace("_cursor=", "_cursor=tampered") })
        {
            using var response = await client.GetAsync(changed);
            var outcome = await ReadAsync<OperationOutcome>(response, HttpStatusCode.BadRequest);
            outcome.Issue.Single().Details!.Coding.Single().Code.Should().Be("invalid_search");
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task MedicalEndpoints_ShouldSeparateAuthenticationFromAuthorizationAndKeepFhirErrors()
    {
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://his-api:5201"), AllowAutoRedirect = false });
        using var missingIdentity = await anonymous.GetAsync($"/fhir/Patient/{factory.PatientId:D}");
        var unauthorized = await ReadAsync<OperationOutcome>(missingIdentity, HttpStatusCode.Unauthorized);
        unauthorized.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Login);
        missingIdentity.Headers.WwwAuthenticate.Should().Contain(x => x.Scheme == "Bearer");
        using var denied = factory.CreateServiceClient("denied");
        using var deniedResponse = await denied.GetAsync($"/fhir/Patient/{factory.PatientId:D}");
        var forbidden = await ReadAsync<OperationOutcome>(deniedResponse, HttpStatusCode.Forbidden);
        forbidden.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Forbidden);
    }

    [Fact]
    public async System.Threading.Tasks.Task Metadata_ShouldDescribeReadSearchAndObservationCreateAndRejectXml()
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://his-api:5201"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("/fhir/metadata");
        var metadata = await ReadAsync<CapabilityStatement>(response);
        metadata.FhirVersion.Should().NotBeNull();
        metadata.Rest.Single().Resource.Should().HaveCount(4);
        metadata.Rest.Single().Resource.Where(resource => resource.Interaction.Any(interaction =>
                interaction.Code == CapabilityStatement.TypeRestfulInteraction.Create)).Select(resource => resource.Type)
            .Should().Equal("Observation");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/fhir+xml");
        using var xml = await client.GetAsync("/fhir/metadata");
        await ReadAsync<OperationOutcome>(xml, HttpStatusCode.NotAcceptable);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
        where T : Resource
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = await response.Content.ReadAsStringAsync();
        return new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .Deserialize<T>(json);
    }
}
