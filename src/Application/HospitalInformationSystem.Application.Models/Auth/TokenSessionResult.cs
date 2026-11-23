using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Models.Auth;

public sealed record TokenSessionResult(
    AuthenticationTokensDto Tokens,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
