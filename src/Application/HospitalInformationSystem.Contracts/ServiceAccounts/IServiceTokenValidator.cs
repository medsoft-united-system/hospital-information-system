using HospitalInformationSystem.Models.ServiceAccounts;
namespace HospitalInformationSystem.Contracts.ServiceAccounts;

public interface IServiceTokenValidator
{
    Task<ServiceAccountIdentity?> ValidateAsync(string accessToken, CancellationToken cancellationToken = default);
}
