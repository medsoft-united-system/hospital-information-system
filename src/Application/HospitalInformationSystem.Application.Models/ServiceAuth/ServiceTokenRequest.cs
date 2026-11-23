using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.ServiceAuth;

public sealed record ServiceTokenRequest(
    [property: JsonPropertyName("apiKey"), JsonRequired] string ApiKey);
