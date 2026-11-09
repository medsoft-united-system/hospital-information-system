using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Domain.LaboratoryTests.Services;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.UpdateLaboratoryTest;

public sealed class UpdateLaboratoryTestCommandHandler(ILaboratoryTestRepository repository,
    IUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IRequestHandler<UpdateLaboratoryTestCommand, LaboratoryTestDefinitionDto>
{
    public async Task<LaboratoryTestDefinitionDto> Handle(UpdateLaboratoryTestCommand request,
        CancellationToken cancellationToken)
    {
        var definition = await repository.FindAsync(request.Id, tracking: true, cancellationToken)
                         ?? throw new EntityNotFoundException("Анализ не найден.");
        LaboratoryTestDefinitionService.Update(definition, request.LoincCode, request.DisplayName,
            timeProvider.GetUtcNow());
        if (await repository.ExistsCodeAsync(definition.LoincCode, definition.Id, cancellationToken))
            throw new BusinessConflictException("Анализ с таким кодом LOINC уже существует.");
        await repository.UpdateAsync(definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return definition.ToDto();
    }
}
