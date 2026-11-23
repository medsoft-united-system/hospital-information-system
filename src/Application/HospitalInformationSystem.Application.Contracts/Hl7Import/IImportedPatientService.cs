using HospitalInformationSystem.Application.Models.Hl7Import;
namespace HospitalInformationSystem.Application.Contracts.Hl7Import;

public interface IImportedPatientService
{
    Task<ImportPatientResult> ImportAsync(ImportPatientRequest request, CancellationToken cancellationToken);
}
