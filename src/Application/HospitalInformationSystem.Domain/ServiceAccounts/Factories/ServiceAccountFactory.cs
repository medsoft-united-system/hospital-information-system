using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.ServiceAccounts.Factories;

public static class ServiceAccountFactory
{
    public static ServiceAccount Create(string name, byte[] apiKeyHash, DateTimeOffset now) =>
        Create(Guid.NewGuid(), name, apiKeyHash, now);

    public static ServiceAccount Create(Guid id, string name, byte[] apiKeyHash, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Укажите идентификатор сервисного аккаунта.");
        var validName = ServiceAccountRules.NormalizeName(name);
        ServiceAccountRules.RequireHash(apiKeyHash);
        var timestamp = ServiceAccountRules.Timestamp(now);
        return new ServiceAccount
        {
            Id = id,
            Name = validName,
            NormalizedName = validName.ToUpperInvariant(),
            Role = ServiceAccountRole.Lis,
            IsActive = true,
            ApiKeyHash = apiKeyHash.ToArray(),
            AuthenticationVersion = 1,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp
        };
    }
}
