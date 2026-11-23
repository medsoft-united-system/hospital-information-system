namespace HospitalInformationSystem.Models.ServiceAccounts;

public sealed record ServiceAccountDto(Guid Id, string Name, string Role, bool IsActive,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, uint Version, string KeyPrefix);

public sealed record ServiceAccountKeyResultDto(ServiceAccountDto Account, string ApiKey, int RevokedSessionCount);

public sealed record ServiceAccountActivityResultDto(ServiceAccountDto Account, int RevokedSessionCount);
