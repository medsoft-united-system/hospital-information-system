using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Contracts.ServiceAuth;

public sealed record ServiceRefreshRequest(
    [property: JsonPropertyName("refreshToken"), JsonRequired] string RefreshToken);
