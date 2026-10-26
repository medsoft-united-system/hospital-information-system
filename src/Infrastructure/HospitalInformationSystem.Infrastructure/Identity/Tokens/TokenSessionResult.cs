using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Infrastructure.Identity.Tokens;

public sealed record TokenSessionResult(
    AuthenticationTokensDto Tokens,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
