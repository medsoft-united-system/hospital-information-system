using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Models.Auth;

public sealed record SessionDto(Guid Id, string UserName, string Role, Guid? MedicalWorkerId,
    MedicalWorkerRole? MedicalWorkerRole = null);
