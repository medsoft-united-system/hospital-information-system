namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed record LaboratoryAnalysisResultRecordedPayload(
    Guid LaboratoryAnalysisRequestId,
    Guid LaboratoryAnalysisResultId,
    Guid PatientId,
    string LoincCode,
    string Value,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    Guid CreatedByServiceAccountId);
