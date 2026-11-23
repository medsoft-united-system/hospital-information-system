using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Contracts.Abstractions;
using HospitalInformationSystem.Infrastructure.ServiceAccounts;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddServiceAccountPersistence(IServiceCollection services)
    {
        services.AddSingleton<IServiceApiKeyGenerator, ServiceApiKeyGenerator>();
        services.AddScoped<IServiceAccountRepository, ServiceAccountRepository>();
        services.AddScoped<IServiceSessionRevoker, ServiceSessionRevoker>();
    }
}
