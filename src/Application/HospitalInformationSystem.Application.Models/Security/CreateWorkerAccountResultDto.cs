namespace HospitalInformationSystem.Application.Models.Security;

public sealed record CreateWorkerAccountResultDto(AccountDetailsDto Account, string GeneratedPassword);
