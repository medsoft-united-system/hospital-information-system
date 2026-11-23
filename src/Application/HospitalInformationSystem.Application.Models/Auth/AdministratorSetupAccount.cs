namespace HospitalInformationSystem.Application.Models.Auth;

public sealed record AdministratorSetupAccount(
    Guid Id,
    string UserName,
    bool IsAdministrator,
    Guid? MedicalWorkerId,
    bool IsActive,
    bool TwoFactorEnabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
