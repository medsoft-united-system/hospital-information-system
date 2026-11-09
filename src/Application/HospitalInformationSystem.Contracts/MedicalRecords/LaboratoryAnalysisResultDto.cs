namespace HospitalInformationSystem.Contracts.MedicalRecords;

public sealed record LaboratoryAnalysisResultDto(
    Guid Id,
    Guid LaboratoryAnalysisRequestId,
    Guid PatientId,
    string Value,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    Guid CreatedByServiceAccountId);
