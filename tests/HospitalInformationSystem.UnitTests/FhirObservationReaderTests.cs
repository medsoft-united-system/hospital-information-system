using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HospitalInformationSystem.Api.Fhir;
using Microsoft.AspNetCore.Http;

namespace HospitalInformationSystem.UnitTests;

public sealed class FhirObservationReaderTests
{
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid PatientId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Theory]
    [InlineData("5.6")]
    [InlineData("0")]
    [InlineData("  Отрицательно 🧪  ")]
    public async Task Read_ShouldReturnVerifiedIdentifiersAndPreserveStringValue(string value)
    {
        var body = Valid();
        body["valueString"] = value;

        var result = await Read(body);

        result.Should().Be(new FhirObservationInput(RequestId, PatientId, "2345-7", value.Trim(),
            new DateTimeOffset(2026, 11, 9, 10, 5, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("2026-11-09T13:05:00+03:00", "2026-11-09T10:05:00Z")]
    [InlineData("2026-11-09T13:05:00.1+03:00", "2026-11-09T10:05:00.1Z")]
    [InlineData("2026-11-09T07:05:00.12-03:00", "2026-11-09T10:05:00.12Z")]
    [InlineData("2026-11-09T10:05:00.123Z", "2026-11-09T10:05:00.123Z")]
    public async Task Read_ShouldNormalizeIssuedTimezoneWithoutInventingPrecision(string issued, string expected)
    {
        var body = Valid();
        body["issued"] = issued;

        var result = await Read(body);

        result.IssuedAtUtc.Should().Be(DateTimeOffset.Parse(expected));
        result.IssuedAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("client-assigned-id")]
    [InlineData("99999999-9999-4999-8999-999999999999")]
    public async Task Read_ShouldIgnoreClientResourceIdAndAllowOrdinaryR5Metadata(string id)
    {
        var body = Valid();
        body["id"] = id;
        body["meta"] = JsonNode.Parse("""{"versionId":"4","tag":[{"system":"urn:example:tag","code":"informational"}]}""");
        body["note"] = JsonNode.Parse("""[{"text":"Информационная заметка"}]""");
        body["extension"] = JsonNode.Parse("""[{"url":"urn:example:info","valueString":"не определяет автора"}]""");

        var result = await Read(body);

        result.RequestId.Should().Be(RequestId);
        result.PatientId.Should().Be(PatientId);
        result.Value.Should().Be("5.6");
    }

    [Theory]
    [InlineData("application/fhir+json")]
    [InlineData("Application/Fhir+Json; charset=UTF-8")]
    [InlineData("application/fhir+json; fhirVersion=5.0")]
    [InlineData("application/fhir+json; charset=\"utf-8\"; fhirVersion=\"5.0.0\"")]
    public async Task Read_ShouldAcceptOnlySupportedFhirJsonMediaParameters(string contentType)
    {
        var request = Request(Valid().ToJsonString(), contentType);

        (await new FhirObservationReader().ReadAsync(request)).LoincCode.Should().Be("2345-7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/json")]
    [InlineData("application/fhir+xml")]
    [InlineData("application/fhir+json; charset=utf-16")]
    [InlineData("application/fhir+json; fhirVersion=4.0")]
    [InlineData("application/fhir+json; charset=utf-8; charset=utf-16")]
    [InlineData("application/fhir+json; fhirVersion=5.0; fhirVersion=5.0.0")]
    [InlineData("application/fhir+json; unsupported=1")]
    public async Task Read_ShouldRejectUnsupportedOrAmbiguousContentType(string? contentType)
    {
        var request = Request(Valid().ToJsonString(), contentType);

        await Reject(request, 415, "unsupported_media_type");
    }

    [Theory]
    [InlineData("resourceType", "\"Patient\"")]
    [InlineData("resourceType", "null")]
    [InlineData("status", "\"preliminary\"")]
    [InlineData("status", "\"FINAL\"")]
    [InlineData("status", "1")]
    [InlineData("valueString", "null")]
    [InlineData("valueString", "5.6")]
    [InlineData("valueString", "\"\"")]
    [InlineData("valueString", "\"  \"")]
    [InlineData("valueString", "\"before\\u0000after\"")]
    [InlineData("issued", "null")]
    [InlineData("issued", "1730000000")]
    [InlineData("code", "{\"concept\":{\"coding\":[{\"system\":\"http://loinc.org\",\"code\":\"2345-7\"}]}}")]
    [InlineData("code", "{\"coding\":[]}")]
    [InlineData("code", "{\"coding\":[{\"system\":\"https://loinc.org\",\"code\":\"2345-7\"}]}")]
    [InlineData("code", "{\"coding\":[{\"system\":\"http://loinc.org\",\"code\":\"bad-code\"}]}")]
    [InlineData("code", "{\"coding\":[{\"system\":\"http://loinc.org\",\"code\":\" 2345-7\"}]}")]
    [InlineData("code", "{\"coding\":[{\"system\":\"http://loinc.org\",\"code\":\"2345-7\"},{\"system\":\"http://loinc.org\",\"code\":\"718-7\"}]}")]
    public async Task Read_ShouldRejectWrongProfileOrPrimitiveTypes(string field, string replacement)
    {
        var body = Valid();
        body[field] = JsonNode.Parse(replacement);

        await Reject(Request(body.ToJsonString()), 400, "invalid_resource");
    }

    [Theory]
    [InlineData("resourceType")]
    [InlineData("status")]
    [InlineData("code")]
    [InlineData("issued")]
    [InlineData("valueString")]
    public async Task Read_ShouldRequireMandatoryClinicalValues(string field)
    {
        var body = Valid();
        body.Remove(field);

        await Reject(Request(body.ToJsonString()), 400, "invalid_resource");
    }

    [Theory]
    [InlineData("valueQuantity", "{\"value\":5.6}")]
    [InlineData("valueInteger", "5")]
    [InlineData("component", "[]")]
    [InlineData("hasMember", "[]")]
    [InlineData("dataAbsentReason", "{\"text\":\"нет данных\"}")]
    [InlineData("modifierExtension", "[]")]
    [InlineData("notAFhirField", "\"PRIVATE-INPUT\"")]
    public async Task Read_ShouldRejectUnsupportedClinicalStructuresAndUnknownFhirFields(string field, string value)
    {
        var body = Valid();
        body[field] = JsonNode.Parse(value);

        await Reject(Request(body.ToJsonString()), 400, "invalid_resource");
    }

    [Fact]
    public async Task Read_ShouldRejectNestedModifierExtensions()
    {
        var body = Valid();
        body["contained"] = JsonNode.Parse("""[{"resourceType":"Patient","id":"nested","modifierExtension":[]}]""");

        await Reject(Request(body.ToJsonString()), 400, "invalid_resource");
    }

    [Theory]
    [InlineData("https://outside.example/fhir/Patient/22222222-2222-4222-8222-222222222222")]
    [InlineData("Practitioner/22222222-2222-4222-8222-222222222222")]
    [InlineData("Patient/00000000-0000-0000-0000-000000000000")]
    [InlineData("Patient/not-a-uuid")]
    [InlineData("Patient/{22222222-2222-4222-8222-222222222222}")]
    [InlineData("Patient/22222222-2222-4222-8222-222222222222/_history/1")]
    [InlineData("Patient/22222222-2222-4222-8222-222222222222?x=1")]
    [InlineData("Patient/22222222-2222-4222-8222-222222222222#fragment")]
    [InlineData("../Patient/22222222-2222-4222-8222-222222222222")]
    [InlineData("urn:uuid:22222222-2222-4222-8222-222222222222")]
    public async Task Read_ShouldAcceptOnlyExactRelativePatientReference(string reference)
    {
        var body = Valid();
        body["subject"]!["reference"] = reference;

        await Reject(Request(body.ToJsonString()), 400, "invalid_reference");
    }

    [Theory]
    [InlineData("ServiceRequest/00000000-0000-0000-0000-000000000000")]
    [InlineData("https://outside.example/fhir/ServiceRequest/11111111-1111-4111-8111-111111111111")]
    [InlineData("Patient/11111111-1111-4111-8111-111111111111")]
    [InlineData("ServiceRequest/11111111-1111-4111-8111-111111111111/extra")]
    public async Task Read_ShouldRequireAnExactServiceRequestReference(string reference)
    {
        var body = Valid();
        body["basedOn"]![0]!["reference"] = reference;

        await Reject(Request(body.ToJsonString()), 400, "invalid_reference");
    }

    [Theory]
    [InlineData("subject", "null")]
    [InlineData("subject", "{}")]
    [InlineData("subject", "{\"reference\":1}")]
    [InlineData("basedOn", "[]")]
    [InlineData("basedOn", "null")]
    [InlineData("basedOn", "[{}]")]
    [InlineData("basedOn", "[{\"reference\":\"ServiceRequest/11111111-1111-4111-8111-111111111111\"},{\"reference\":\"ServiceRequest/11111111-1111-4111-8111-111111111111\"}]")]
    public async Task Read_ShouldRejectMissingWrongTypedOrMultipleReferences(string field, string value)
    {
        var body = Valid();
        body[field] = JsonNode.Parse(value);

        await Reject(Request(body.ToJsonString()), 400, "invalid_reference");
    }

    [Theory]
    [InlineData("2026-11-09")]
    [InlineData("2026-11-09T10:05Z")]
    [InlineData("2026-11-09T10:05:00")]
    [InlineData("2026-11-09 10:05:00Z")]
    [InlineData("2026-11-09T10:05:00.1230Z")]
    [InlineData("2026-11-09T10:05:00,123Z")]
    [InlineData("2026-11-09T10:05:00+0300")]
    [InlineData("2026-11-09T10:05:00+24:00")]
    [InlineData("2026-11-09T10:05:00+03:60")]
    [InlineData("2026-02-30T10:05:00Z")]
    [InlineData("0000-11-09T10:05:00Z")]
    [InlineData("2026-11-09T10:05:60Z")]
    public async Task Read_ShouldRejectUnzonedImpreciseOrInvalidIssued(string issued)
    {
        var body = Valid();
        body["issued"] = issued;

        await Reject(Request(body.ToJsonString()), 400, "invalid_resource");
    }

    [Fact]
    public async Task Read_ShouldCountUnicodeScalarValuesRatherThanUtf16CodeUnits()
    {
        var body = Valid();
        body["valueString"] = string.Concat(Enumerable.Repeat("🧪", 2000));

        (await Read(body)).Value.EnumerateRunes().Count().Should().Be(2000);

        body["valueString"] = string.Concat(Enumerable.Repeat("🧪", 2001));
        await Reject(Request(body.ToJsonString()), 400, "invalid_resource");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("{\"resourceType\":\"Observation\",}")]
    public async Task Read_ShouldRejectMalformedJsonOrNonObjectRoot(string json)
    {
        await Reject(Request(json), 400, "invalid_resource");
    }

    [Theory]
    [InlineData("\"status\":\"final\"", "\"status\":\"final\",\"status\":\"final\"")]
    [InlineData("\"system\":\"http://loinc.org\"", "\"system\":\"http://loinc.org\",\"system\":\"http://loinc.org\"")]
    [InlineData("\"valueString\"", "\"ValueString\"")]
    [InlineData("\"reference\"", "\"Reference\"")]
    [InlineData("\"valueString\":\"5.6\"", "\"valueString\":\"\\uD800\"")]
    [InlineData("\"valueString\":\"5.6\"", "\"valueString\":\"\\uDC00\"")]
    public async Task Read_ShouldRejectDuplicateMembersCaseAliasesAndInvalidSurrogates(string from, string to)
    {
        var json = Valid().ToJsonString().Replace(from, to, StringComparison.Ordinal);

        await Reject(Request(json), 400, from.Contains("reference", StringComparison.Ordinal) ?
            "invalid_reference" : "invalid_resource");
    }

    [Fact]
    public async Task Read_ShouldRejectInvalidUtf8WithoutReflectingInput()
    {
        var request = Request("");
        request.Body = new MemoryStream([0x7b, 0x22, 0xff, 0x22, 0x7d]);

        await Reject(request, 400, "invalid_resource");
    }

    [Fact]
    public async Task Read_ShouldRejectKnownOversizeBeforeReadingBody()
    {
        var request = Request(Valid().ToJsonString());
        request.ContentLength = 65537;

        await Reject(request, 413, "request_too_large");

        request.Body.Position.Should().Be(0);
    }

    [Fact]
    public async Task Read_ShouldBoundUnknownLengthBodyAndLeaveStreamOwnedByServer()
    {
        var request = Request(new string(' ', 70000));
        request.ContentLength = null;

        await Reject(request, 413, "request_too_large");

        request.Body.Position.Should().Be(65537);
        request.Body.CanRead.Should().BeTrue();
    }

    [Fact]
    public async Task Read_ShouldAcceptAValidDocumentAtExactByteLimit()
    {
        var json = Valid().ToJsonString();
        var request = Request(json + new string(' ', 65536 - Encoding.UTF8.GetByteCount(json)));

        (await new FhirObservationReader().ReadAsync(request)).RequestId.Should().Be(RequestId);
    }

    [Fact]
    public async Task Read_ShouldPropagateCancellationWithoutReadingOrReclassifyingIt()
    {
        var request = Request(Valid().ToJsonString());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var read = () => new FhirObservationReader().ReadAsync(request, canceled.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        request.Body.Position.Should().Be(0);
    }

    [Fact]
    public async Task Read_ShouldUseSafeErrorForIncompleteBody()
    {
        var request = Request("");
        request.Body = new UnreadableBody();

        await Reject(request, 400, "invalid_resource");
    }

    private static JsonObject Valid() => new()
    {
        ["resourceType"] = "Observation",
        ["status"] = "final",
        ["code"] = JsonNode.Parse("""{"coding":[{"system":"http://loinc.org","code":"2345-7"}]}"""),
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{PatientId:D}" },
        ["basedOn"] = new JsonArray(new JsonObject { ["reference"] = $"ServiceRequest/{RequestId:D}" }),
        ["issued"] = "2026-11-09T10:05:00Z",
        ["valueString"] = "5.6"
    };

    private static Task<FhirObservationInput> Read(JsonObject body) =>
        new FhirObservationReader().ReadAsync(Request(body.ToJsonString()));

    private static HttpRequest Request(string body, string? contentType = FhirResponseWriter.ContentType)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }

    private static async Task Reject(HttpRequest request, int status, string machineCode)
    {
        var read = () => new FhirObservationReader().ReadAsync(request);
        var thrown = (await read.Should().ThrowAsync<FhirHttpException>()).Which;
        thrown.StatusCode.Should().Be(status);
        thrown.MachineCode.Should().Be(machineCode);
        thrown.Message.Should().NotContain("PRIVATE-INPUT").And.NotContain("PRIVATE-STREAM-ERROR");
    }

    private sealed class UnreadableBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("PRIVATE-STREAM-ERROR"));
    }
}
