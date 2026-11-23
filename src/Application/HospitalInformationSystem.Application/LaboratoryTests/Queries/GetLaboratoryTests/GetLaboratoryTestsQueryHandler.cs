using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.LaboratoryTests.Mapping;
using HospitalInformationSystem.Application.Models.LaboratoryTests;
using MediatR;

namespace HospitalInformationSystem.Application.LaboratoryTests.Queries.GetLaboratoryTests;

public sealed class GetLaboratoryTestsQueryHandler(ILaboratoryTestRepository repository)
    : IRequestHandler<GetLaboratoryTestsQuery, IReadOnlyList<LaboratoryTestDefinitionDto>>
{
    public async Task<IReadOnlyList<LaboratoryTestDefinitionDto>> Handle(GetLaboratoryTestsQuery request,
        CancellationToken cancellationToken) =>
        (await repository.ListAsync(request.ActiveOnly, cancellationToken)).Select(x => x.ToDto()).ToArray();
}
