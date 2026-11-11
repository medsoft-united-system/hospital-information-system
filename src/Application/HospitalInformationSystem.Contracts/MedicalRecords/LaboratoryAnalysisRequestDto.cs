namespace HospitalInformationSystem.Contracts.MedicalRecords;

public sealed record LaboratoryAnalysisRequestDto(
    Guid Id,
    Guid MedicalRecordId,
    Guid PatientId,
    Guid RequestedByMedicalWorkerId,
    Guid LaboratoryTestDefinitionId,
    string LoincCode,
    string DisplayName,
    string? Comment,
    DateTimeOffset AuthoredAtUtc,
    LaboratoryAnalysisRequestStatus Status,
    LaboratoryAnalysisResultDto? Result);
