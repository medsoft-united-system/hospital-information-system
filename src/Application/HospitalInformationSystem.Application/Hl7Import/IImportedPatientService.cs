namespace HospitalInformationSystem.Application.Hl7Import;

public interface IImportedPatientService
{
    Task<ImportPatientResult> ImportAsync(ImportPatientCommand command, CancellationToken cancellationToken);
}
