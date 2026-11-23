namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed class LaboratoryAnalysisRequest
{
    public Guid Id { get; init; }
    public Guid MedicalRecordId { get; init; }
    public Guid PatientId { get; init; }
    public Guid RequestedByMedicalWorkerId { get; init; }
    public Guid LaboratoryTestDefinitionId { get; init; }
    public required string LoincCodeSnapshot { get; init; }
    public required string DisplayNameSnapshot { get; init; }
    public string? Comment { get; init; }
    public DateTimeOffset AuthoredAtUtc { get; init; }
    public LaboratoryAnalysisRequestStatus Status { get; set; } = LaboratoryAnalysisRequestStatus.Active;
    public LaboratoryAnalysisResult? Result { get; set; }
}
