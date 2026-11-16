using HospitalInformationSystem.Domain.ServiceAccounts;

namespace HospitalInformationSystem.Infrastructure.Persistence.Configurations;

internal static class ServiceAccountRoleStorage
{
    public static string Write(ServiceAccountRole role) => role switch
    {
        ServiceAccountRole.Lis => "LIS",
        ServiceAccountRole.Hl7Import => "HL7Import",
        _ => throw new InvalidOperationException("Неизвестная роль сервисного аккаунта.")
    };

    public static ServiceAccountRole Read(string role) => role switch
    {
        "LIS" => ServiceAccountRole.Lis,
        "HL7Import" => ServiceAccountRole.Hl7Import,
        _ => throw new InvalidOperationException("Неизвестная роль сервисного аккаунта в БД.")
    };
}
