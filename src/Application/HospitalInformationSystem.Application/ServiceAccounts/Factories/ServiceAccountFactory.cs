using HospitalInformationSystem.Application.Models.Common;
using HospitalInformationSystem.Application.Models.ServiceAccounts;

namespace HospitalInformationSystem.Application.ServiceAccounts.Factories;

public static class ServiceAccountFactory
{
    public static ServiceAccount Create(string name, byte[] apiKeyHash, DateTimeOffset now,
        ServiceAccountRole role = ServiceAccountRole.Lis) =>
        Create(Guid.NewGuid(), name, apiKeyHash, now, role);

    public static ServiceAccount Create(Guid id, string name, byte[] apiKeyHash, DateTimeOffset now,
        ServiceAccountRole role = ServiceAccountRole.Lis)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Укажите идентификатор сервисного аккаунта.");
        var validName = ServiceAccountRules.NormalizeName(name);
        ServiceAccountRules.RequireHash(apiKeyHash);
        ServiceAccountRules.RequireRole(role);
        var timestamp = ServiceAccountRules.Timestamp(now);
        return new ServiceAccount
        {
            Id = id,
            Name = validName,
            NormalizedName = validName.ToUpperInvariant(),
            Role = role,
            IsActive = true,
            ApiKeyHash = apiKeyHash.ToArray(),
            AuthenticationVersion = 1,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp
        };
    }
}
