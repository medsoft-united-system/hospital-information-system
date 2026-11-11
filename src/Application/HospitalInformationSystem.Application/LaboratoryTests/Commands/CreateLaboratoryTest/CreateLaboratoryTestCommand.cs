using HospitalInformationSystem.Contracts.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.CreateLaboratoryTest;

public sealed record CreateLaboratoryTestCommand(string LoincCode, string DisplayName)
    : IRequest<LaboratoryTestDefinitionDto>;
