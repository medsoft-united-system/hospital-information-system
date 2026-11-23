namespace HospitalInformationSystem.Models.Persistence;

public sealed class PatientExternalIdentifierDbo
{
    public string SourceSystem { get; set; } = string.Empty;
    public string ExternalPatientId { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
