using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Hl7.Fhir.Serialization;
using Bundle = Hl7.Fhir.Model.Bundle;
using Observation = Hl7.Fhir.Model.Observation;
using Resource = Hl7.Fhir.Model.Resource;

namespace HospitalInformationSystem.IntegrationTests;

public sealed class FhirMutableQueueApiTests(FhirObservationCreateApiFactory factory)
    : IClassFixture<FhirObservationCreateApiFactory>
{
    [Fact]
    public async Task Search_AfterAnotherOperatorCompletesTail_ShouldOmitTotalOnEmptyContinuation()
    {
        var states = new[]
        {
            await factory.CreateScenarioAsync(),
            await factory.CreateScenarioAsync(),
            await factory.CreateScenarioAsync()
        };
        using var client = factory.CreateServiceClient();
        using var firstResponse = await client.GetAsync("/fhir/ServiceRequest?status=active&_count=1");
        var first = await ReadAsync<Bundle>(firstResponse);
        var firstId = first.Entry.Single().Resource!.Id;
        var continuation = first.Link.Single(x => x.Relation == "next").Url;
        foreach (var state in states.Where(x => x.RequestId.ToString("D") != firstId))
        {
            var body = JsonSerializer.Serialize(new
            {
                resourceType = "Observation",
                status = "final",
                code = new { coding = new[] { new { system = "http://loinc.org", code = state.LoincCode } } },
                subject = new { reference = $"Patient/{state.PatientId:D}" },
                basedOn = new[] { new { reference = $"ServiceRequest/{state.RequestId:D}" } },
                issued = "2026-11-09T12:05:00.000Z",
                valueString = "5.6"
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/fhir+json");
            using var completedResponse = await client.PostAsync("/fhir/Observation", content);
            await ReadAsync<Observation>(completedResponse, HttpStatusCode.Created);
        }

        using var continuedResponse = await client.GetAsync(continuation);

        var continued = await ReadAsync<Bundle>(continuedResponse);
        continued.Entry.Should().BeEmpty();
        continued.Total.Should().BeNull();
        continued.Link.Should().NotContain(x => x.Relation == "next");
        using var raw = JsonDocument.Parse(await continuedResponse.Content.ReadAsStringAsync());
        raw.RootElement.TryGetProperty("total", out _).Should().BeFalse();
        using var remainingResponse = await client.GetAsync("/fhir/ServiceRequest?status=active&_count=1");
        (await ReadAsync<Bundle>(remainingResponse)).Entry.Should().ContainSingle().Which.Resource!.Id.Should().Be(firstId);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
        where T : Resource
    {
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, json);
        return new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .Deserialize<T>(json);
    }
}
