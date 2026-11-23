namespace HospitalInformationSystem.Models.Security;

public sealed record ResetAccountPasswordResultDto(
    Guid AccountId,
    string GeneratedPassword,
    int RevokedRefreshTokenCount);
