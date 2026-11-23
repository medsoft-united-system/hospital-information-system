using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Models.MedicalRecords;

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
    LaboratoryAnalysisResultDto? Result,
    string? RequestedByNameSnapshot = null);
