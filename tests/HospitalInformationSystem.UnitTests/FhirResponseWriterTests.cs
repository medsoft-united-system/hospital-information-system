using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Api.Fhir;
using HospitalInformationSystem.Models.Fhir;
using Microsoft.AspNetCore.Http;
using Resource = Hl7.Fhir.Model.Resource;

namespace HospitalInformationSystem.UnitTests;

public sealed class FhirResponseWriterTests
{
    [Fact]
    public async Task Write_ShouldUseFhirJsonUtf8AndPreventCaching()
    {
        var context = new DefaultHttpContext();
        await using var stream = new MemoryStream();
        context.Response.Body = stream;
        var outcome = FhirResourceMapper.OperationOutcome(404, "not_found", "Запись не найдена.");

        await new FhirResponseWriter().WriteAsync(context, outcome, 404, CancellationToken.None);

        context.Response.StatusCode.Should().Be(404);
        context.Response.ContentType.Should().Be("application/fhir+json");
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))!;
        json["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
        json["issue"]![0]!["diagnostics"]!.GetValue<string>().Should().Be("Запись не найдена.");
        json.AsObject().Should().NotContainKey("Issue");
    }

    [Fact]
    public async Task Write_CanceledRequest_ShouldNotStartOrPartiallyWriteResponse()
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = 202;
        await using var stream = new MemoryStream();
        context.Response.Body = stream;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var write = () => new FhirResponseWriter().WriteAsync(context,
            FhirResourceMapper.OperationOutcome(400, "invalid_search", "Некорректный поиск."), 400,
            cancellation.Token);

        await write.Should().ThrowAsync<OperationCanceledException>();
        stream.Length.Should().Be(0);
        context.Response.StatusCode.Should().Be(202);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Serialize_ShouldKeepInstantLexicalUtcForStandaloneAndBundledObservation(bool bundled)
    {
        var requestId = Guid.Parse("11111111-abcd-4111-8111-111111111111");
        var timestamp = new DateTimeOffset(2026, 11, 9, 13, 11, 12, 123, TimeSpan.FromHours(3)).AddTicks(4567);
        const string value = "Значение 2026-11-09T13:11:12.123+03:00";
        var source = new FhirObservationReadModel(Guid.NewGuid(), requestId, Guid.NewGuid(),
            "2345-7", "Глюкоза", value, timestamp);
        var observation = FhirResourceMapper.ToObservation(source);
        var originalLexical = observation.IssuedElement!.JsonValue;
        Resource resource = bundled
            ? FhirResourceMapper.SearchBundle("Observation", [observation],
                $"https://his-api:5201/fhir/Observation?based-on=ServiceRequest%2F{requestId:D}", null)
            : observation;

        var serialized = await new FhirResponseWriter().SerializeAsync(resource);

        var json = JsonNode.Parse(serialized)!;
        var result = bundled ? json["entry"]![0]!["resource"]! : json;
        result["issued"]!.GetValue<string>().Should().Be("2026-11-09T10:11:12.123Z");
        result["valueString"]!.GetValue<string>().Should().Be(value);
        observation.IssuedElement!.JsonValue.Should().Be(originalLexical);
        source.IssuedAtUtc.Should().Be(timestamp);
        new FhirJsonDeserializer(new DeserializerSettings().UsingMode(DeserializationMode.Strict))
            .DeserializeResource(serialized).GetType().Should()
            .Be(bundled ? typeof(Hl7.Fhir.Model.Bundle) : typeof(Hl7.Fhir.Model.Observation));
    }
}
