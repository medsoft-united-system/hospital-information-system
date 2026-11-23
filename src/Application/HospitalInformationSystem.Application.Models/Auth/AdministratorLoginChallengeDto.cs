namespace HospitalInformationSystem.Application.Models.Auth;

public sealed record AdministratorLoginChallengeDto(string UserName, DateTimeOffset ExpiresAtUtc);
