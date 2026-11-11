using HospitalInformationSystem.Domain.ServiceAccounts;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

internal static class ServiceAccountRoleStorage
{
    public static string Write(ServiceAccountRole role) => role switch
    {
        ServiceAccountRole.Lis => "LIS",
        _ => throw new InvalidOperationException("Неизвестная роль сервисного аккаунта.")
    };

    public static ServiceAccountRole Read(string role) => role switch
    {
        "LIS" => ServiceAccountRole.Lis,
        _ => throw new InvalidOperationException("Неизвестная роль сервисного аккаунта в БД.")
    };
}
