namespace HospitalInformationSystem.Models.Security;

public sealed record CreateWorkerAccountResultDto(AccountDetailsDto Account, string GeneratedPassword);
