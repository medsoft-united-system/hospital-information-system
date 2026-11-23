using HospitalInformationSystem.Application.Models.Fhir;
using HospitalInformationSystem.Application.Models.Patients;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Queries;

public sealed record GetFhirPatientQuery(Guid Id) : IRequest<Patient>;

public sealed record GetFhirServiceRequestQuery(Guid Id) : IRequest<FhirServiceRequestReadModel>;
