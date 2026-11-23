using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.ServiceAccounts.Services;

public static class ServiceAccountDomainService
{
    public static bool Rename(ServiceAccount account, string name, DateTimeOffset now)
    {
        var validName = ServiceAccountRules.NormalizeName(name);
        if (account.Name == validName) return false;
        account.Name = validName;
        account.NormalizedName = validName.ToUpperInvariant();
        account.UpdatedAtUtc = UpdatedAt(account, now);
        return true;
    }

    public static bool SetActive(ServiceAccount account, bool isActive, DateTimeOffset now)
    {
        if (account.IsActive == isActive) return false;
        var version = isActive ? account.AuthenticationVersion : NextVersion(account);
        account.IsActive = isActive;
        account.AuthenticationVersion = version;
        account.UpdatedAtUtc = UpdatedAt(account, now);
        return true;
    }

    public static void RotateKey(ServiceAccount account, byte[] hash, DateTimeOffset now)
    {
        ServiceAccountRules.RequireHash(hash);
        var version = NextVersion(account);
        account.ApiKeyHash = hash.ToArray();
        account.AuthenticationVersion = version;
        account.UpdatedAtUtc = UpdatedAt(account, now);
    }

    private static long NextVersion(ServiceAccount account)
    {
        if (account.AuthenticationVersion is < 1 or long.MaxValue)
            throw new BusinessRuleConflictException("Версия безопасности сервисного аккаунта не допускает изменение.");
        return account.AuthenticationVersion + 1;
    }

    private static DateTimeOffset UpdatedAt(ServiceAccount account, DateTimeOffset now)
    {
        var at = ServiceAccountRules.Timestamp(now);
        if (at < account.CreatedAtUtc) at = account.CreatedAtUtc;
        return at < account.UpdatedAtUtc ? account.UpdatedAtUtc : at;
    }
}
