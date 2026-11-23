using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.ServiceAccounts;
using Riok.Mapperly.Abstractions;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

[Mapper]
internal static partial class ServiceAccountDtoMapper
{
    [MapperIgnoreSource(nameof(ServiceAccountDbo.NormalizedName))]
    [MapperIgnoreSource(nameof(ServiceAccountDbo.ApiKeyHash))]
    [MapperIgnoreSource(nameof(ServiceAccountDbo.AuthenticationVersion))]
    public static partial ServiceAccountReadRow ToReadRow(ServiceAccountDbo source, uint version);

    public static partial ServiceAccountDto ToDto(ServiceAccountReadRow source, string keyPrefix);

    public static string ToRole(ServiceAccountRole role) => role switch
    {
        ServiceAccountRole.Lis => ServiceAccountRoles.Lis,
        ServiceAccountRole.Hl7Import => ServiceAccountRoles.Hl7Import,
        _ => throw new InvalidOperationException("Неизвестная роль сервисного аккаунта.")
    };
}
