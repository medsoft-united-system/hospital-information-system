using HospitalInformationSystem.Application.Abstractions;
using HospitalInformationSystem.Application.Hl7Import;
using HospitalInformationSystem.Infrastructure.Hl7Import;
using HospitalInformationSystem.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalInformationSystem.Infrastructure.DI;

public static partial class DependencyInjection
{
    private static void AddClinicalPersistence(IServiceCollection services)
    {
        services.AddScoped<IMedicalWorkerRepository, MedicalWorkerRepository>();
        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IImportedPatientService, ImportedPatientService>();
        services.AddScoped<IDiagnosisRepository, DiagnosisRepository>();
        services.AddScoped<MedicalRecordRepository>();
        services.AddScoped<IMedicalRecordRepository>(provider => provider.GetRequiredService<MedicalRecordRepository>());
        services.AddScoped<IMedicalRecordPresence>(provider => provider.GetRequiredService<MedicalRecordRepository>());
    }
}
