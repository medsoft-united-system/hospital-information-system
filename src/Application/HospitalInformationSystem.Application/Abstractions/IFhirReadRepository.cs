using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.Patients;

namespace HospitalInformationSystem.Application.Abstractions;

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
