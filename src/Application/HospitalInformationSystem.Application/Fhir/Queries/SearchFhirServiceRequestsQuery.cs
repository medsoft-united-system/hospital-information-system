using HospitalInformationSystem.Application.Models.Fhir;
using HospitalInformationSystem.Application.Models.MedicalRecords;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Queries;

public sealed record SearchFhirServiceRequestsQuery(LaboratoryAnalysisRequestStatus? Status, int Count,
    FhirPagePosition? After) : IRequest<FhirReadPage<FhirServiceRequestReadModel>>;
