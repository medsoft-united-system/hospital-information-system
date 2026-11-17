namespace HospitalInformationSystem.Domain.MedicalRecords;

public sealed record DiagnosisAddedPayload(
    Guid MedicalRecordDiagnosisId,
    Guid DiagnosisDefinitionId,
    string Code,
    string Name,
    DateTimeOffset? DiagnosedAtUtc = null,
    int? DiagnosedAtOffsetMinutes = null,
    string? CodingSystem = null,
    string? DiagnosisType = null,
    Guid? AddedByMedicalWorkerId = null,
    Guid? AddedByServiceAccountId = null);
