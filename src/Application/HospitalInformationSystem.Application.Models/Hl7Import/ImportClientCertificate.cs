namespace HospitalInformationSystem.Application.Models.Hl7Import;

public sealed record ImportClientCertificate(
    string Sha256Fingerprint,
    string Subject,
    string Issuer,
    string SerialNumber,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc);
