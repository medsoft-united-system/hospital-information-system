using HospitalInformationSystem.Models.LaboratoryTests;

namespace HospitalInformationSystem.Abstractions.Repositories;

public interface ILaboratoryTestRepository
{
    Task<IReadOnlyList<LaboratoryTestDefinition>> ListAsync(bool activeOnly, CancellationToken cancellationToken);
    Task<LaboratoryTestDefinition?> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken);
    Task<bool> ExistsCodeAsync(string code, Guid? excludingId, CancellationToken cancellationToken);
    void Add(LaboratoryTestDefinition definition);
    Task UpdateAsync(LaboratoryTestDefinition definition, CancellationToken cancellationToken);
}
