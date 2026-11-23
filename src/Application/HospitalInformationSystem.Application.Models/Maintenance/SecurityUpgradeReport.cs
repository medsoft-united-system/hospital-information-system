namespace HospitalInformationSystem.Application.Models.Maintenance;

public sealed record SecurityUpgradeReport(IReadOnlyList<AccountUpgradeReport> Accounts,
    IReadOnlyList<Guid> MedicalWorkerIds, bool RequiresAdministratorSelection);
