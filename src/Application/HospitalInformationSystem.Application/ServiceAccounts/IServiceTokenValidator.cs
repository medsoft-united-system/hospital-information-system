namespace HospitalInformationSystem.Application.ServiceAccounts;

public interface IServiceTokenValidator
{
    Task<ServiceAccountIdentity?> ValidateAsync(string accessToken, CancellationToken cancellationToken = default);
}
