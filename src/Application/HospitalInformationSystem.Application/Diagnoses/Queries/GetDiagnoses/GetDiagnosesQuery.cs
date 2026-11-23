using HospitalInformationSystem.Models.Diagnoses;
using MediatR;

namespace HospitalInformationSystem.Application.Diagnoses.Queries.GetDiagnoses;

public sealed record GetDiagnosesQuery : IRequest<IReadOnlyList<DiagnosisDefinitionDto>>;

