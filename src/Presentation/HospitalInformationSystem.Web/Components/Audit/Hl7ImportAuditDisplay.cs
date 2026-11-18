namespace HospitalInformationSystem.Web.Components.Audit;

public sealed record Hl7ImportAuditDisplay(
    string ResultLabel,
    string ResultClass,
    string? ServiceName,
    Guid? ServiceId,
    string? MessageId,
    string? SourceSystem,
    string? ExternalPatientId,
    Guid? PatientId,
    int? AcceptedDiagnoses,
    string? CertificateSha256,
    string? CertificateSubject,
    string? CertificateIssuer,
    string? CertificateSerial,
    DateTimeOffset? CertificateNotBefore,
    DateTimeOffset? CertificateNotAfter);
