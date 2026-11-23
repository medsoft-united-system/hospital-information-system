using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Models.ServiceAuth;

public sealed record ServiceRefreshRequest(
    [property: JsonPropertyName("refreshToken"), JsonRequired] string RefreshToken);
