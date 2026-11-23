using HospitalInformationSystem.Application.Models.ServiceAccounts;
namespace HospitalInformationSystem.Application.Contracts.ServiceAccounts;

public interface IServiceTokenValidator
{
    Task<ServiceAccountIdentity?> ValidateAsync(string accessToken, CancellationToken cancellationToken = default);
}
