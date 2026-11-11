using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Infrastructure.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddAuditPersistence(IServiceCollection services)
    {
        services.TryAddSingleton<IAuditActorAccessor, SystemAuditActorAccessor>();
        services.AddSingleton<AuditEventFactory>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
    }
}
