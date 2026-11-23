using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.ServiceAuth;

public sealed record Hl7ServiceConnectionStatusDto(
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("serviceAccountId"), JsonRequired] Guid ServiceAccountId,
    [property: JsonPropertyName("role"), JsonRequired] string Role,
    [property: JsonPropertyName("hl7Version"), JsonRequired] string Hl7Version);

public sealed record HumanConnectionStatusDto(
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("accountId"), JsonRequired] Guid AccountId,
    [property: JsonPropertyName("role"), JsonRequired] string Role,
    [property: JsonPropertyName("accountKind"), JsonRequired] string AccountKind);
