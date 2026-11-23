using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.ServiceAuth;

public sealed record ServiceConnectionStatusDto(
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("serviceAccountId"), JsonRequired] Guid ServiceAccountId,
    [property: JsonPropertyName("role"), JsonRequired] string Role,
    [property: JsonPropertyName("fhirVersion"), JsonRequired] string FhirVersion);
