using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddFhirReadPersistence(IServiceCollection services) =>
        services.AddScoped<IFhirReadRepository, FhirReadRepository>();
}
