using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hl7.Fhir.Serialization;
using HospitalInformationSystem.Api.Security;
using HospitalInformationSystem.Domain.LaboratoryTests;
using HospitalInformationSystem.Domain.MedicalRecords;
using Microsoft.Net.Http.Headers;
using Observation = Hl7.Fhir.Model.Observation;

namespace HospitalInformationSystem.Api.Fhir;

public sealed partial class FhirObservationReader
{
    private const int MaxBodyBytes = (int)ApiSecurityRegistration.MaxJsonBodyBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<FhirObservationInput> ReadAsync(HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContentType(request.ContentType);
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();
        var bytes = await ReadBodyAsync(request.Body, cancellationToken);
        try
        {
            var json = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidResource();
            ValidateJsonTree(root);
            if (RequiredString(root, "resourceType") != "Observation" ||
                RequiredString(root, "status") != "final")
                throw InvalidResource();
            foreach (var property in root.EnumerateObject())
                if (property.Name.StartsWith("value", StringComparison.Ordinal) && property.Name != "valueString" ||
                    property.Name is "component" or "hasMember" or "dataAbsentReason")
                    throw InvalidResource();

            var patientId = Reference(root, "subject", "Patient");
            if (!root.TryGetProperty("basedOn", out var basedOn) ||
                basedOn.ValueKind != JsonValueKind.Array || basedOn.GetArrayLength() != 1)
                throw InvalidReference();
            var requestId = Reference(basedOn[0], "ServiceRequest");
            var loincCode = ReadLoinc(root);
            var value = RequiredString(root, "valueString");
            if (!LaboratoryAnalysisRules.IsValidValue(value)) throw InvalidResource();
            var issued = ReadIssued(RequiredString(root, "issued"));

            var deserializer = new FhirJsonDeserializer(
                new DeserializerSettings().UsingMode(DeserializationMode.Strict));
            if (deserializer.DeserializeResource(json) is not Observation) throw InvalidResource();
            cancellationToken.ThrowIfCancellationRequested();
            return new FhirObservationInput(requestId, patientId, loincCode,
                LaboratoryAnalysisRules.NormalizeValue(value), issued);
        }
        catch (Exception exception) when (exception is JsonException or DeserializationFailedException or
                                             DecoderFallbackException or FormatException or ArgumentException or
                                             InvalidOperationException)
        {
            throw InvalidResource();
        }
    }

    private static void ValidateContentType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media) ||
            !string.Equals(media.MediaType.Value, FhirResponseWriter.ContentType, StringComparison.OrdinalIgnoreCase))
            throw UnsupportedMediaType();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in media.Parameters)
        {
            var name = parameter.Name.Value ?? string.Empty;
            var value = HeaderUtilities.RemoveQuotes(parameter.Value).Value;
            if (!names.Add(name) || !(name.Equals("charset", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(value, "utf-8", StringComparison.OrdinalIgnoreCase) ||
                                     name.Equals("fhirVersion", StringComparison.OrdinalIgnoreCase) &&
                                     value is "5.0" or "5.0.0"))
                throw UnsupportedMediaType();
        }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = Math.Min(buffer.Length, MaxBodyBytes + 1 - (int)body.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
                if (read == 0) return body.ToArray();
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (body.Length > MaxBodyBytes) throw TooLarge();
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            throw TooLarge();
        }
        catch (IOException)
        {
            throw InvalidResource();
        }
    }

    private static void ValidateJsonTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name == "modifierExtension") throw InvalidResource();
                ValidateUnicode(property.Name);
                ValidateJsonTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateJsonTree(child);
        else if (element.ValueKind == JsonValueKind.String)
            ValidateUnicode(element.GetString()!);
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[++index]))
                throw InvalidResource();
        }
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw InvalidResource();
        return value.GetString()!;
    }

    private static Guid Reference(JsonElement parent, string property, string type)
    {
        if (!parent.TryGetProperty(property, out var reference)) throw InvalidReference();
        return Reference(reference, type);
    }

    private static Guid Reference(JsonElement reference, string type)
    {
        if (reference.ValueKind != JsonValueKind.Object ||
            !reference.TryGetProperty("reference", out var value) || value.ValueKind != JsonValueKind.String)
            throw InvalidReference();
        var text = value.GetString()!;
        var prefix = type + "/";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length != prefix.Length + 36 ||
            !Guid.TryParseExact(text[prefix.Length..], "D", out var id) || id == Guid.Empty)
            throw InvalidReference();
        return id;
    }

    private static string ReadLoinc(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Object ||
            !code.TryGetProperty("coding", out var coding) || coding.ValueKind != JsonValueKind.Array ||
            coding.GetArrayLength() != 1)
            throw InvalidResource();
        if (RequiredString(coding[0], "system") != FhirResourceMapper.LoincSystem) throw InvalidResource();
        var value = RequiredString(coding[0], "code");
        if (value != value.Trim() || !LaboratoryTestDefinitionRules.IsWellFormedCode(value)) throw InvalidResource();
        return value;
    }

    private static DateTimeOffset ReadIssued(string value)
    {
        if (!IssuedFormat().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued) ||
            !LaboratoryAnalysisRules.IsValidIssuedAt(issued))
            throw InvalidResource();
        return issued.ToUniversalTime();
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,3})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IssuedFormat();

    private static FhirHttpException InvalidResource() =>
        new(400, "invalid_resource", "Observation не соответствует согласованному профилю FHIR R5.");

    private static FhirHttpException InvalidReference() =>
        new(400, "invalid_reference", "Укажите относительные ссылки Patient/UUID и ServiceRequest/UUID.");

    private static FhirHttpException UnsupportedMediaType() =>
        new(415, "unsupported_media_type", "Для Observation требуется application/fhir+json в UTF-8 и FHIR R5.");

    private static FhirHttpException TooLarge() =>
        new(413, "request_too_large", "Размер Observation превышает допустимый.");
}
