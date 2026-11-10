using HospitalInformationSystem.Application.ServiceAccounts;

namespace HospitalInformationSystem.Application.Abstractions;

public interface IServiceApiKeyGenerator
{
    GeneratedServiceApiKey Generate(Guid serviceAccountId);
}
