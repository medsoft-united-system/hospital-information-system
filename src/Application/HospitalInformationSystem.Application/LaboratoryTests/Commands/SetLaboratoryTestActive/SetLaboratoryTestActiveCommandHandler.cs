using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Abstractions.UnitOfWork;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Application.LaboratoryTests.Services;
using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.SetLaboratoryTestActive;

public sealed class SetLaboratoryTestActiveCommandHandler(ILaboratoryTestRepository repository,
    IUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IRequestHandler<SetLaboratoryTestActiveCommand, LaboratoryTestDefinitionDto>
{
    public async Task<LaboratoryTestDefinitionDto> Handle(SetLaboratoryTestActiveCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Version == 0)
            throw new DomainValidationException("Укажите текущую версию анализа.");
        var definition = await repository.FindAsync(request.Id, tracking: true, cancellationToken)
                         ?? throw new EntityNotFoundException("Анализ не найден.");
        if (definition.Version != request.Version)
            throw new BusinessConflictException("Анализ уже изменён. Обновите справочник и повторите действие.");
        if (definition.IsActive == request.IsActive)
            return definition.ToDto();
        LaboratoryTestDefinitionService.SetActive(definition, request.IsActive, timeProvider.GetUtcNow());
        await repository.UpdateAsync(definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = await repository.FindAsync(definition.Id, tracking: false, cancellationToken)
                    ?? throw new EntityNotFoundException("Анализ не найден после сохранения.");
        return saved.ToDto();
    }
}
