namespace HospitalInformationSystem.Application.Models.Hl7Import;

public enum PatientImportConflict
{
    DiagnosisName,
    ExternalIdentity
}

public sealed class PatientImportConflictException(PatientImportConflict conflict, Exception innerException)
    : Exception("Конфликт данных при импорте пациента.", innerException)
{
    public PatientImportConflict Conflict { get; } = conflict;
}
