using HospitalInformationSystem.Application.Models.MedicalWorkers;
using MediatR;

namespace HospitalInformationSystem.Application.Fhir.Queries;

public sealed record GetFhirPractitionerQuery(Guid Id) : IRequest<MedicalWorker>;
