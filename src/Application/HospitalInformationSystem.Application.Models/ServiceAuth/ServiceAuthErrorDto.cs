using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.ServiceAuth;

public sealed record ServiceAuthErrorDto(
    [property: JsonPropertyName("code"), JsonRequired] string Code,
    [property: JsonPropertyName("message"), JsonRequired] string Message,
    [property: JsonPropertyName("traceId"), JsonRequired] string TraceId);
