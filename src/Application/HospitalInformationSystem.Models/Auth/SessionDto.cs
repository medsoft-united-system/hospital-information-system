namespace HospitalInformationSystem.Models.Auth;

public sealed record SessionDto(Guid Id, string UserName, string Role, Guid? MedicalWorkerId,
    HospitalInformationSystem.Models.Security.MedicalWorkerRole? MedicalWorkerRole = null);
