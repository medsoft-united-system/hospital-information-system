namespace HospitalInformationSystem.Domain.MedicalRecords;

public sealed record LaboratoryAnalysisRequestedPayload(
    Guid LaboratoryAnalysisRequestId,
    Guid MedicalRecordId,
    Guid PatientId,
    Guid RequestedByMedicalWorkerId,
    Guid LaboratoryTestDefinitionId,
    string LoincCode,
    string DisplayName,
    string? Comment,
    DateTimeOffset AuthoredAtUtc);
