namespace HospitalInformationSystem.Application.Models.Security;

public sealed record AuthenticationTokensDto(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    AuthenticationSessionDto Session);
