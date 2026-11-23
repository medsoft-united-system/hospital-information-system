namespace HospitalInformationSystem.Application.Models.MedicalRecords;

public enum MedicalRecordEventType
{
    MedicalRecordCreated,
    DiagnosisAdded,
    DiagnosisRemoved,
    PrescriptionAdded,
    PrescriptionCancelled,
    LaboratoryAnalysisRequested,
    LaboratoryAnalysisResultRecorded
}
