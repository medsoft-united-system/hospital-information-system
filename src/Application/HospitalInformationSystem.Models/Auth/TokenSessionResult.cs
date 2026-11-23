using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Models.Auth;

public sealed record TokenSessionResult(
    AuthenticationTokensDto Tokens,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
