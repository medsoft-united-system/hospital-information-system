using HospitalInformationSystem.Domain.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.Persistence.Mappings;

[Mapper(UseDeepCloning = true)]
internal static partial class ServiceAccountDboMapper
{
    public static partial ServiceAccount ToDomain(this ServiceAccountDbo dbo);
    public static partial ServiceAccountDbo ToDbo(this ServiceAccount entity);

    [MapperIgnoreSource(nameof(ServiceAccount.Id))]
    [MapperIgnoreSource(nameof(ServiceAccount.CreatedAtUtc))]
    [MapperIgnoreTarget(nameof(ServiceAccountDbo.Id))]
    [MapperIgnoreTarget(nameof(ServiceAccountDbo.CreatedAtUtc))]
    public static partial void UpdateDbo(this ServiceAccount entity, ServiceAccountDbo dbo);
}
