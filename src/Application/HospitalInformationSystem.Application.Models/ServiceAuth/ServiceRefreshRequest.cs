using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.ServiceAuth;

public sealed record ServiceRefreshRequest(
    [property: JsonPropertyName("refreshToken"), JsonRequired] string RefreshToken);
