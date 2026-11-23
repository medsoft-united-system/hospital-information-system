using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.ServiceAuth;

public sealed record ServiceTokenRequest(
    [property: JsonPropertyName("apiKey"), JsonRequired] string ApiKey);
