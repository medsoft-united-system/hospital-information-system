using HospitalInformationSystem.Application.Models.Diagnoses;
using MediatR;

namespace HospitalInformationSystem.Application.Diagnoses.Queries.GetDiagnosis;

public sealed record GetDiagnosisQuery(Guid Id) : IRequest<DiagnosisDefinitionDto>;

