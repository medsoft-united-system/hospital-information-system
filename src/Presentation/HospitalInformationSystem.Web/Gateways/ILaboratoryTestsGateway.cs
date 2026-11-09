using HospitalInformationSystem.Contracts.LaboratoryTests;

namespace HospitalInformationSystem.Web.Gateways;

public interface ILaboratoryTestsGateway
{
    Task<IReadOnlyList<LaboratoryTestDefinitionDto>> ListAsync(CancellationToken cancellationToken,
        bool activeOnly = true);
    Task<LaboratoryTestDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<LaboratoryTestDefinitionDto> SaveAsync(Guid? id, SaveLaboratoryTestRequest request,
        CancellationToken cancellationToken);
    Task<LaboratoryTestDefinitionDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken);
}
