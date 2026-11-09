namespace HospitalInformationSystem.Domain.MedicalRecords;

public sealed class LaboratoryAnalysisResult
{
    public Guid Id { get; init; }
    public Guid LaboratoryAnalysisRequestId { get; init; }
    public Guid PatientId { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public Guid CreatedByServiceAccountId { get; init; }
}
