namespace HospitalInformationSystem.Contracts.Auth;

public sealed record AdministratorLoginChallengeDto(string UserName, DateTimeOffset ExpiresAtUtc);
