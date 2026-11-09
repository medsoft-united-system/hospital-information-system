using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Initializers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<HospitalDbContext>(options => options.UseNpgsql(connectionString));
        AddIdentityPersistence(services);
        AddAuditPersistence(services);
        AddClinicalPersistence(services);
        AddLaboratoryTestPersistence(services);
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<HospitalDbContext>());
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
