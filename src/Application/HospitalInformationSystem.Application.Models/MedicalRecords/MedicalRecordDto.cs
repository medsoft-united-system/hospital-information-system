using HospitalInformationSystem.Application.Models.MedicalRecords;

namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public sealed record MedicalRecordDto(
    Guid Id,
    Guid PatientId,
    DateTimeOffset CreatedAtUtc,
    long Version,
    IReadOnlyCollection<MedicalRecordDiagnosisDto> Diagnoses,
    IReadOnlyCollection<PrescriptionDto> Prescriptions)
{
    public IReadOnlyCollection<LaboratoryAnalysisRequestDto> LaboratoryAnalysisRequests { get; init; } = [];
}
