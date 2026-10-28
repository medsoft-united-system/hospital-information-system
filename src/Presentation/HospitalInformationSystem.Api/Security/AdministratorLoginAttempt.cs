namespace HospitalInformationSystem.Api.Security;

public sealed record AdministratorLoginAttempt(string Id, Guid UserId, string UserName, string SecurityStamp,
    DateTimeOffset ExpiresAtUtc, int FailedAttempts = 0);
