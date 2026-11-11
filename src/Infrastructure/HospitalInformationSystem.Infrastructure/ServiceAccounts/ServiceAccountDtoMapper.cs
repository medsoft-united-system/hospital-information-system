using HospitalInformationSystem.Contracts.ServiceAccounts;
using HospitalInformationSystem.Domain.ServiceAccounts;
using HospitalInformationSystem.Infrastructure.Persistence.Models;
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

    private static string ToRole(ServiceAccountRole role) => role == ServiceAccountRole.Lis ? "LIS" :
        throw new InvalidOperationException("Неизвестная роль сервисного аккаунта.");
}
