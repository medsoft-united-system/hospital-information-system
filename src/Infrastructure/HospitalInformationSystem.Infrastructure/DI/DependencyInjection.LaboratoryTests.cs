using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Infrastructure.Initializers;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddLaboratoryTestPersistence(IServiceCollection services)
    {
        services.AddScoped<ILaboratoryTestRepository, LaboratoryTestRepository>();
        services.AddScoped<LaboratoryTestCatalogInitializer>();
    }
}
