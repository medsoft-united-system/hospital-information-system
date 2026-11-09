using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTest;

public sealed class GetLaboratoryTestQueryHandler(ILaboratoryTestRepository repository)
    : IRequestHandler<GetLaboratoryTestQuery, LaboratoryTestDefinitionDto>
{
    public async Task<LaboratoryTestDefinitionDto> Handle(GetLaboratoryTestQuery request,
        CancellationToken cancellationToken)
    {
        var definition = await repository.FindAsync(request.Id, tracking: false, cancellationToken)
                         ?? throw new EntityNotFoundException("Анализ не найден.");
        return definition.ToDto();
    }
}
