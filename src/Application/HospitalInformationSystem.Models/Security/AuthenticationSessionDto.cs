namespace HospitalInformationSystem.Models.Security;

public sealed record AuthenticationSessionDto(
    Guid Id,
    string UserName,
    string AccountRole,
    Guid? MedicalWorkerId,
    MedicalWorkerRole? MedicalWorkerRole);
