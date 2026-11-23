using HospitalInformationSystem.Models.Hl7Import;
namespace HospitalInformationSystem.Contracts.Hl7Import;

public interface IImportedPatientService
{
    Task<ImportPatientResult> ImportAsync(ImportPatientRequest request, CancellationToken cancellationToken);
}
