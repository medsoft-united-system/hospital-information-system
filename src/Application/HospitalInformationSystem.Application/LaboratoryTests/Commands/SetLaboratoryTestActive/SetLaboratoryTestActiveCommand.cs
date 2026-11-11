using HospitalInformationSystem.Contracts.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.SetLaboratoryTestActive;

public sealed record SetLaboratoryTestActiveCommand(Guid Id, bool IsActive, uint Version = 0) : IRequest<LaboratoryTestDefinitionDto>;
