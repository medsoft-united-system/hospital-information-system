using FluentValidation;
using HospitalInformationSystem.Application.Audit.Services;
using HospitalInformationSystem.Application.Auth.Services;
using HospitalInformationSystem.Application.Common;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Application.MedicalRecords;
using HospitalInformationSystem.Application.MedicalRecords.Snapshot;
using HospitalInformationSystem.Application.ServiceAccounts;
using HospitalInformationSystem.Application.ServiceAccounts.Services;
using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Contracts.Hl7Import;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Hl7Import;
using HospitalInformationSystem.Models.ServiceAccounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HospitalInformationSystem.Application.DI;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICurrentServiceAccount, UnavailableCurrentServiceAccount>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IAdministratorSetupService, AdministratorSetupService>();
        services.AddScoped<IServiceAccountService, ServiceAccountService>();
        services.AddSingleton<AuditEntryFactory>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IImportedPatientService, ImportedPatientService>();
        services.AddScoped<IHl7ImportFailureAuditor, Hl7ImportFailureAuditor>();
        services.AddScoped<MedicalRecordCommandContext>();
        services.AddScoped<IMedicalRecordSnapshotBuilder, MedicalRecordSnapshotBuilder>();
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        return services;
    }
}
