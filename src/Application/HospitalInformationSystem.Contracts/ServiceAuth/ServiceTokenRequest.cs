using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Contracts.ServiceAuth;

public sealed record ServiceTokenRequest(
    [property: JsonPropertyName("apiKey"), JsonRequired] string ApiKey);
