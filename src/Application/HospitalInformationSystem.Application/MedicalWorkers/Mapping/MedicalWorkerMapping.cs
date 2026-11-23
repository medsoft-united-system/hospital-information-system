using HospitalInformationSystem.Application.Models.MedicalWorkers;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Application.MedicalWorkers.Mapping;

[Mapper]
internal static partial class MedicalWorkerMapping
{
    [MapperIgnoreSource(nameof(MedicalWorker.CreatedAtUtc))]
    [MapperIgnoreSource(nameof(MedicalWorker.UpdatedAtUtc))]
    public static partial MedicalWorkerDto ToDto(this MedicalWorker worker);

    [MapEnum(EnumMappingStrategy.ByName)]
    public static partial MedicalWorkerRole ToDomain(this Models.Security.MedicalWorkerRole role);

    [MapEnum(EnumMappingStrategy.ByName)]
    public static partial Models.Security.MedicalWorkerRole ToContract(this MedicalWorkerRole role);
}
