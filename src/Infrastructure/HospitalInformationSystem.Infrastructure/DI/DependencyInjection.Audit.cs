using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Infrastructure.Audit;
using HospitalInformationSystem.Infrastructure.Hl7Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddAuditPersistence(IServiceCollection services)
    {
        services.TryAddSingleton<IAuditActorAccessor, SystemAuditActorAccessor>();
        services.AddSingleton<AuditEventFactory>();
        services.AddScoped<ImportAuditContext>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IAuditActorScope>(provider => provider.GetRequiredService<ImportAuditContext>());
    }
}
