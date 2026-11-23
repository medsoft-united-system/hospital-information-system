using HospitalInformationSystem.Application.Models.MedicalWorkers;

namespace HospitalInformationSystem.Application.Models.Persistence;

public sealed class MedicalWorkerDbo
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public MedicalWorkerRole Role { get; set; } = MedicalWorkerRole.Other;
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
