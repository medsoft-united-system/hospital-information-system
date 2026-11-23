namespace HospitalInformationSystem.Models.Auth;

public sealed record AdministratorLoginChallengeDto(string UserName, DateTimeOffset ExpiresAtUtc);
