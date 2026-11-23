using HospitalInformationSystem.Models.Fhir;
using HospitalInformationSystem.Models.MedicalRecords;
using HospitalInformationSystem.Models.MedicalWorkers;
using HospitalInformationSystem.Models.Patients;

namespace HospitalInformationSystem.Abstractions.Repositories;

public interface IFhirReadRepository
{
    Task<Patient?> GetPatientAsync(Guid id, CancellationToken cancellationToken);
    Task<MedicalWorker?> GetPractitionerAsync(Guid id, CancellationToken cancellationToken);
    Task<FhirServiceRequestReadModel?> GetServiceRequestAsync(Guid id, CancellationToken cancellationToken);
    Task<FhirObservationReadModel?> GetObservationAsync(Guid id, CancellationToken cancellationToken);
    Task<FhirReadPage<FhirServiceRequestReadModel>> SearchServiceRequestsAsync(
        LaboratoryAnalysisRequestStatus? status, int count, FhirPagePosition? after,
        CancellationToken cancellationToken);
    Task<FhirReadPage<FhirObservationReadModel>> SearchObservationsAsync(Guid basedOn, int count,
        FhirPagePosition? after, CancellationToken cancellationToken);
}
