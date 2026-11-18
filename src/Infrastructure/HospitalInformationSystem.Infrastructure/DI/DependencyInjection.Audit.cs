using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Application.Hl7Import;
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
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IHl7ImportFailureAuditor, Hl7ImportFailureAuditor>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
    }
}
