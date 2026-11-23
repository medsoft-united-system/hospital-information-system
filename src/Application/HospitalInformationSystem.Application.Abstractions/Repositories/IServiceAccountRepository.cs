using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Abstractions.Repositories;

public interface IServiceAccountRepository
{
    Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken);
    Task<ServiceAccountDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<(ServiceAccount Account, uint Version)?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(string normalizedName, Guid? exceptId, CancellationToken cancellationToken);
    void Add(ServiceAccount account);
    void Update(ServiceAccount account);
    ServiceAccountDto Describe(ServiceAccount account);
}
