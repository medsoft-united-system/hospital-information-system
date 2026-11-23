using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.ServiceAuth;

public sealed record ServiceTokenPairDto(
    [property: JsonPropertyName("accessToken"), JsonRequired] string AccessToken,
    [property: JsonPropertyName("accessTokenExpiresAt"), JsonRequired,
        JsonConverter(typeof(ServiceAuthTimestampJsonConverter))] DateTimeOffset AccessTokenExpiresAt,
    [property: JsonPropertyName("refreshToken"), JsonRequired] string RefreshToken,
    [property: JsonPropertyName("refreshTokenExpiresAt"), JsonRequired,
        JsonConverter(typeof(ServiceAuthTimestampJsonConverter))] DateTimeOffset RefreshTokenExpiresAt);
