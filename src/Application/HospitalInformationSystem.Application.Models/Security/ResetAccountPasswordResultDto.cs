namespace HospitalInformationSystem.Application.Models.Security;

public sealed record ResetAccountPasswordResultDto(
    Guid AccountId,
    string GeneratedPassword,
    int RevokedRefreshTokenCount);
