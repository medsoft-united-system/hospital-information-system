using HospitalInformationSystem.Abstractions.Transactions;
using HospitalInformationSystem.Abstractions.UnitOfWork;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Infrastructure.Initializers;
using HospitalInformationSystem.Infrastructure.Persistence;
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
        AddFhirReadPersistence(services);
        AddServiceAccountPersistence(services);
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<HospitalDbContext>());
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
