using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Contracts.Abstractions;

public interface IServiceApiKeyGenerator
{
    GeneratedServiceApiKey Generate(Guid serviceAccountId);
    byte[] Hash(string apiKey);
    ServiceApiKeyVerificationResult Verify(string? apiKey, Guid serviceAccountId, byte[] expectedHash);
}
