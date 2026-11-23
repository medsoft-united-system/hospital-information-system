using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Application.Auth.Services;

[Mapper(EnumMappingStrategy = EnumMappingStrategy.ByName)]
internal static partial class AccountMapping
{
    public static partial HospitalInformationSystem.Models.Security.MedicalWorkerRole ToContractRole(
        HospitalInformationSystem.Models.MedicalWorkers.MedicalWorkerRole role);
}
