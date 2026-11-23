using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.Contracts.Abstractions;

public interface IServiceAccountService
{
    Task<IReadOnlyList<ServiceAccountDto>> ListAsync(CancellationToken cancellationToken);
    Task<ServiceAccountDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ServiceAccountKeyResultDto> CreateAsync(CreateServiceAccountRequest request, CancellationToken cancellationToken);
    Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountRequest request, CancellationToken cancellationToken);
    Task<ServiceAccountActivityResultDto> SetActiveAsync(Guid id, SetServiceAccountActiveRequest request,
        CancellationToken cancellationToken);
    Task<ServiceAccountKeyResultDto> RotateKeyAsync(Guid id, RotateServiceAccountKeyRequest request,
        CancellationToken cancellationToken);
}
