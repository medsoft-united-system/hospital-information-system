namespace HospitalInformationSystem.Application.Models.Persistence;

public sealed class LaboratoryAnalysisResultDbo
{
    public Guid Id { get; set; }
    public Guid LaboratoryAnalysisRequestId { get; set; }
    public Guid PatientId { get; set; }
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public Guid CreatedByServiceAccountId { get; set; }
}
