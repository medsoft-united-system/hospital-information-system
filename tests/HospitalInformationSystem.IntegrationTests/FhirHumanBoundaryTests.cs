using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Contracts.Auth;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirHumanBoundaryTests(SecureHospitalApiFactory factory) : IClassFixture<SecureHospitalApiFactory>
{
    [Fact]
    public async System.Threading.Tasks.Task GenuineHumanJwt_ShouldRemainValidForHumanApiButNotAuthenticateAsService()
    {
        using var client = factory.CreateCookieClient();
        var csrf = (await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/auth/csrf"))!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RequestToken);
        (await client.PostAsJsonAsync("/api/auth/login", factory.AdministratorLogin())).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var route in new[] { "Patient/" + Guid.NewGuid(), "Practitioner/" + Guid.NewGuid(),
                     "ServiceRequest/" + Guid.NewGuid(), "Observation/" + Guid.NewGuid(), "ServiceRequest",
                     "Observation?based-on=ServiceRequest/" + Guid.NewGuid() })
        {
            using var response = await client.GetAsync("/fhir/" + route);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
            var outcome = Parse<OperationOutcome>(await response.Content.ReadAsStringAsync());
            outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.Login);
        }
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var create = await client.PostAsync("/fhir/Observation", new StringContent("{}",
            System.Text.Encoding.UTF8, "application/fhir+json"));
        create.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Parse<OperationOutcome>(await create.Content.ReadAsStringAsync()).Issue.Single().Code
            .Should().Be(OperationOutcome.IssueType.Login);
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = await client.GetAsync("/fhir/metadata");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        Parse<CapabilityStatement>(await metadata.Content.ReadAsStringAsync())
            .FhirVersion.Should().Be(FHIRVersion.N5_0_0);
    }

    [Fact]
    public async System.Threading.Tasks.Task ServiceScheme_ShouldRejectArbitraryBearerAndKeepMetadataOpen()
    {
        using var client = factory.CreateCookieClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        using var response = await client.GetAsync("/fhir/ServiceRequest");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        (await client.GetAsync("/fhir/metadata")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static T Parse<T>(string json) where T : Resource =>
        new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .Deserialize<T>(json);
}
