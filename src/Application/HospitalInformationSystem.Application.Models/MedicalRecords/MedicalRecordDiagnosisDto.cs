namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed record MedicalRecordDiagnosisDto(
    Guid Id,
    string Code,
    string Name,
    DateTimeOffset AddedAtUtc,
    Guid? AddedByMedicalWorkerId,
    Guid? AddedByServiceAccountId = null,
    DateTimeOffset? DiagnosedAtUtc = null,
    int? DiagnosedAtOffsetMinutes = null,
    string? CodingSystem = null,
    string? DiagnosisType = null);
