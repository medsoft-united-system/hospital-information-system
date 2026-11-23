namespace HospitalInformationSystem.Models.Hl7Import;

public abstract record ImportPatientResult;

public sealed record PatientImported(Guid PatientId, int DiagnosisCount) : ImportPatientResult;

public sealed record PatientImportRejected(
    PatientImportRejection Reason,
    string SafeMessage) : ImportPatientResult;

public enum PatientImportRejection
{
    Duplicate,
    Validation
}
