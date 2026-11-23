namespace HospitalInformationSystem.Models.MedicalWorkers;

public sealed record MedicalWorkerDto(Guid Id, string FirstName, string LastName, string Position, bool IsDeleted,
    Security.MedicalWorkerRole? Role = null);
