using HospitalInformationSystem.Domain.MedicalRecords;

namespace HospitalInformationSystem.Infrastructure.Persistence.Models;

public sealed class LaboratoryAnalysisRequestDbo
{
    public Guid Id { get; set; }
    public Guid MedicalRecordId { get; set; }
    public Guid PatientId { get; set; }
    public Guid RequestedByMedicalWorkerId { get; set; }
    public Guid LaboratoryTestDefinitionId { get; set; }
    public string LoincCodeSnapshot { get; set; } = string.Empty;
    public string DisplayNameSnapshot { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public DateTimeOffset AuthoredAtUtc { get; set; }
    public LaboratoryAnalysisRequestStatus Status { get; set; }
    public LaboratoryAnalysisResultDbo? Result { get; set; }
}
