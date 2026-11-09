using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Domain.LaboratoryTests.Services;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.SetLaboratoryTestActive;

public sealed class SetLaboratoryTestActiveCommandHandler(ILaboratoryTestRepository repository,
    IUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IRequestHandler<SetLaboratoryTestActiveCommand, LaboratoryTestDefinitionDto>
{
    public async Task<LaboratoryTestDefinitionDto> Handle(SetLaboratoryTestActiveCommand request,
        CancellationToken cancellationToken)
    {
        var definition = await repository.FindAsync(request.Id, tracking: true, cancellationToken)
                         ?? throw new EntityNotFoundException("Анализ не найден.");
        if (definition.IsActive == request.IsActive)
            return definition.ToDto();
        LaboratoryTestDefinitionService.SetActive(definition, request.IsActive, timeProvider.GetUtcNow());
        await repository.UpdateAsync(definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return definition.ToDto();
    }
}
