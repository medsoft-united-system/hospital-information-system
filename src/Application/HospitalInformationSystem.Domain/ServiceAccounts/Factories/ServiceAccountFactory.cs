using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.ServiceAccounts.Factories;

public static class ServiceAccountFactory
{
    public static ServiceAccount Create(string name, byte[] apiKeyHash, DateTimeOffset now)
    {
        var validName = name?.Trim();
        TextValidation.RejectNullCharacter(validName, "Имя сервисного аккаунта");
        if (validName is null || validName.EnumerateRunes().Count() is < 3 or > 64)
            throw new DomainValidationException("Имя сервисного аккаунта: требуется от 3 до 64 символов.");
        if (apiKeyHash is null || apiKeyHash.Length != 32)
            throw new DomainValidationException("Хеш API key должен содержать 32 байта SHA-256.");

        var timestamp = now.ToUniversalTime();
        return new ServiceAccount
        {
            Id = Guid.NewGuid(),
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
