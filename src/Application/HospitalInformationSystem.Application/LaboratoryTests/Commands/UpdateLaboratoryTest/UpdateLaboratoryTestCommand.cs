using HospitalInformationSystem.Models.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.UpdateLaboratoryTest;

public sealed record UpdateLaboratoryTestCommand(Guid Id, string LoincCode, string DisplayName, uint Version = 0)
    : IRequest<LaboratoryTestDefinitionDto>;
