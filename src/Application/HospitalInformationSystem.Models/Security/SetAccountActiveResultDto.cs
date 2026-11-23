namespace HospitalInformationSystem.Models.Security;

public sealed record SetAccountActiveResultDto(AccountDetailsDto Account, int RevokedRefreshTokenCount);
