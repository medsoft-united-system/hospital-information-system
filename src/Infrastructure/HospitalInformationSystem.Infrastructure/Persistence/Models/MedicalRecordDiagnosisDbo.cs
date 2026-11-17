namespace HospitalInformationSystem.Infrastructure.Persistence.Models;

public sealed class MedicalRecordDiagnosisDbo
{
    public Guid Id { get; set; }
    public Guid MedicalRecordId { get; set; }
    public Guid? DiagnosisDefinitionId { get; set; }
    public string DiagnosisCodeSnapshot { get; set; } = string.Empty;
    public string DiagnosisNameSnapshot { get; set; } = string.Empty;
    public DateTimeOffset AddedAtUtc { get; set; }
    public Guid? AddedByMedicalWorkerId { get; set; }
    public Guid? AddedByServiceAccountId { get; set; }
    public DateTimeOffset? DiagnosedAtUtc { get; set; }
    public int? DiagnosedAtOffsetMinutes { get; set; }
    public string? CodingSystem { get; set; }
    public string? DiagnosisType { get; set; }
    public DateTimeOffset? RemovedAtUtc { get; set; }
    public Guid? RemovedByMedicalWorkerId { get; set; }
}
