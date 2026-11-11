using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Contracts.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTests;

public sealed class GetLaboratoryTestsQueryHandler(ILaboratoryTestRepository repository)
    : IRequestHandler<GetLaboratoryTestsQuery, IReadOnlyList<LaboratoryTestDefinitionDto>>
{
    public async Task<IReadOnlyList<LaboratoryTestDefinitionDto>> Handle(GetLaboratoryTestsQuery request,
        CancellationToken cancellationToken) =>
        (await repository.ListAsync(request.ActiveOnly, cancellationToken)).Select(x => x.ToDto()).ToArray();
}
