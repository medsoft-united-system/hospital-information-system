using HospitalInformationSystem.Application.Models.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTest;

public sealed record GetLaboratoryTestQuery(Guid Id) : IRequest<LaboratoryTestDefinitionDto>;
