using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Identity;

[Mapper(EnumMappingStrategy = EnumMappingStrategy.ByName)]
internal static partial class AccountMapping
{
    public static partial HospitalInformationSystem.Contracts.Security.MedicalWorkerRole ToContractRole(
        HospitalInformationSystem.Domain.MedicalWorkers.MedicalWorkerRole role);
}
