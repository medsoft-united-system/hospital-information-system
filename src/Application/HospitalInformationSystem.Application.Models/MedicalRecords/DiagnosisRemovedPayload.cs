namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed record DiagnosisRemovedPayload(Guid MedicalRecordDiagnosisId, string Code, string Name);
