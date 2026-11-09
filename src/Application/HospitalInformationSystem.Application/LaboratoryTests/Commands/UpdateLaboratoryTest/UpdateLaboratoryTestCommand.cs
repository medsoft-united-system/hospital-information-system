using HospitalInformationSystem.Contracts.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.UpdateLaboratoryTest;

public sealed record UpdateLaboratoryTestCommand(Guid Id, string LoincCode, string DisplayName)
    : IRequest<LaboratoryTestDefinitionDto>;
