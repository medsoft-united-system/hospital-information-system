namespace HospitalInformationSystem.Application.Audit;

public sealed record AuditMetadata
{
    public string? Action { get; init; }
    public Guid? TargetId { get; init; }
    public string? CorrelationId { get; init; }
    public string? RevokeReason { get; init; }
    public int? RevokedRefreshTokenCount { get; init; }
    public string? AttemptedUserName { get; init; }
    public string? RemoteIp { get; init; }
    public Guid? MedicalWorkerId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? ServiceAccountId { get; init; }
    public int? RevokedServiceSessionCount { get; init; }
    public Guid? RecordEntryId { get; init; }
    public string? Outcome { get; init; }
    public string? SourceSystem { get; init; }
    public string? ExternalPatientId { get; init; }
    public int? DiagnosisCount { get; init; }
    public string? ClientCertificateSha256 { get; init; }
    public string? ClientCertificateSubject { get; init; }
    public string? ClientCertificateIssuer { get; init; }
    public string? ClientCertificateSerialNumber { get; init; }
    public DateTimeOffset? ClientCertificateNotBeforeUtc { get; init; }
    public DateTimeOffset? ClientCertificateNotAfterUtc { get; init; }
}
