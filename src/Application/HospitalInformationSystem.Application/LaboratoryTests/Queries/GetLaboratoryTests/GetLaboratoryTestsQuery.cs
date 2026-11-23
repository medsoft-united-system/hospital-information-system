using HospitalInformationSystem.Models.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTests;

public sealed record GetLaboratoryTestsQuery(bool ActiveOnly = true)
    : IRequest<IReadOnlyList<LaboratoryTestDefinitionDto>>;
