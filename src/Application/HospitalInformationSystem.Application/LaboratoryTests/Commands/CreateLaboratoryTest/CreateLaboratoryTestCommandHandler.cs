using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using HospitalInformationSystem.Domain.LaboratoryTests.Factories;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Commands.CreateLaboratoryTest;

public sealed class CreateLaboratoryTestCommandHandler(ILaboratoryTestRepository repository,
    IUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IRequestHandler<CreateLaboratoryTestCommand, LaboratoryTestDefinitionDto>
{
    public async Task<LaboratoryTestDefinitionDto> Handle(CreateLaboratoryTestCommand request,
        CancellationToken cancellationToken)
    {
        var definition = LaboratoryTestDefinitionFactory.Create(request.LoincCode, request.DisplayName,
            timeProvider.GetUtcNow());
        if (await repository.ExistsCodeAsync(definition.LoincCode, null, cancellationToken))
            throw new BusinessConflictException("Анализ с таким кодом LOINC уже существует.");
        repository.Add(definition);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = await repository.FindAsync(definition.Id, tracking: false, cancellationToken)
                    ?? throw new EntityNotFoundException("Анализ не найден после сохранения.");
        return saved.ToDto();
    }
}
