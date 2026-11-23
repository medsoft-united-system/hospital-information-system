using HospitalInformationSystem.Application.Models.Fhir;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Queries;

public sealed record GetFhirObservationQuery(Guid Id) : IRequest<FhirObservationReadModel>;
