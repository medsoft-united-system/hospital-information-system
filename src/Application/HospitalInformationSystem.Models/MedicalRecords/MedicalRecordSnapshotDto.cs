using HospitalInformationSystem.Models.MedicalRecords;

namespace HospitalInformationSystem.Models.MedicalRecords;

public sealed record MedicalRecordSnapshotDto(
    Guid MedicalRecordId,
    Guid PatientId,
    DateTimeOffset At,
    long Version,
    IReadOnlyCollection<MedicalRecordDiagnosisDto> Diagnoses,
    IReadOnlyCollection<PrescriptionDto> Prescriptions)
{
    public IReadOnlyCollection<LaboratoryAnalysisRequestDto> LaboratoryAnalysisRequests { get; init; } = [];
}
