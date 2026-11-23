namespace HospitalInformationSystem.Models.Auth;

public sealed record AccountIdentity(
    Guid Id,
    string? UserName,
    Guid? MedicalWorkerId,
    bool IsAdministrator,
    bool IsActive,
    DateTimeOffset? LockoutEnd,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<string> Roles);
