using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Infrastructure.Hl7Import;

internal static class Hl7ImportAuditMetadataFactory
{
    public static AuditMetadata Create(string outcome, ImportClientCertificate certificate,
        Guid? serviceAccountId, string? sourceSystem, string? externalPatientId,
        Guid? patientId, int diagnosisCount)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (outcome is not (Hl7ImportAuditOutcomes.Success or Hl7ImportAuditOutcomes.Duplicate or
            Hl7ImportAuditOutcomes.ValidationFailed or Hl7ImportAuditOutcomes.AuthenticationRejected or
            Hl7ImportAuditOutcomes.ProtocolRejected or Hl7ImportAuditOutcomes.InternalError))
            throw new DomainValidationException("Недопустимый результат импорта для аудита.");
        if (certificate.Sha256Fingerprint is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit) ||
            certificate.NotBeforeUtc == default || certificate.NotAfterUtc <= certificate.NotBeforeUtc)
            throw new DomainValidationException("Недопустимые данные клиентского сертификата для аудита.");
        if (serviceAccountId == Guid.Empty || patientId == Guid.Empty ||
            diagnosisCount is < 0 or > Hl7ImportProtocol.MaxDiagnoses)
            throw new DomainValidationException("Недопустимые идентификаторы или число диагнозов для аудита.");

        return new AuditMetadata
        {
            Action = "hl7-patient-import",
            TargetId = patientId,
            ServiceAccountId = serviceAccountId,
            Outcome = outcome,
            SourceSystem = sourceSystem == Hl7ImportProtocol.SourceSystem ? sourceSystem : null,
            ExternalPatientId = SafeValue(externalPatientId, 128),
            DiagnosisCount = diagnosisCount,
            ClientCertificateSha256 = certificate.Sha256Fingerprint,
            ClientCertificateSubject = SafeValue(certificate.Subject, 2048),
            ClientCertificateIssuer = SafeValue(certificate.Issuer, 2048),
            ClientCertificateSerialNumber = SafeValue(certificate.SerialNumber, 128),
            ClientCertificateNotBeforeUtc = certificate.NotBeforeUtc.ToUniversalTime(),
            ClientCertificateNotAfterUtc = certificate.NotAfterUtc.ToUniversalTime()
        };
    }

    private static string? SafeValue(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) ? value : null;
}
