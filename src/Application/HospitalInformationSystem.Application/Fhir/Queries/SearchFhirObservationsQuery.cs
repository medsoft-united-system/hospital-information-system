using HospitalInformationSystem.Application.Models.Fhir;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Queries;

public sealed record SearchFhirObservationsQuery(Guid BasedOn, int Count, FhirPagePosition? After)
    : IRequest<FhirReadPage<FhirObservationReadModel>>;
