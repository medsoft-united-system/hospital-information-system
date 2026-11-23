using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Application.Auth.Services;

[Mapper(EnumMappingStrategy = EnumMappingStrategy.ByName)]
internal static partial class AccountMapping
{
    public static partial Models.Security.MedicalWorkerRole ToContractRole(
        Models.MedicalWorkers.MedicalWorkerRole role);
}
