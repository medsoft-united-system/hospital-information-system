using HospitalInformationSystem.Domain.MedicalRecords;
using HospitalInformationSystem.Domain.MedicalWorkers;
using HospitalInformationSystem.Domain.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir;

public sealed record GetFhirPatientQuery(Guid Id) : IRequest<Patient>;
public sealed record GetFhirPractitionerQuery(Guid Id) : IRequest<MedicalWorker>;
public sealed record GetFhirServiceRequestQuery(Guid Id) : IRequest<FhirServiceRequestReadModel>;
public sealed record GetFhirObservationQuery(Guid Id) : IRequest<FhirObservationReadModel>;
public sealed record SearchFhirServiceRequestsQuery(LaboratoryAnalysisRequestStatus? Status, int Count,
    FhirPagePosition? After) : IRequest<FhirReadPage<FhirServiceRequestReadModel>>;
public sealed record SearchFhirObservationsQuery(Guid BasedOn, int Count, FhirPagePosition? After)
    : IRequest<FhirReadPage<FhirObservationReadModel>>;
