using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.ServiceAccounts;

public static class ServiceAccountRules
{
    public const int LegacyHashLength = 32;
    public const int MinEncodedHashLength = 90;
    public const int MaxEncodedHashLength = 128;
    public const int MinNameLength = 3;
    public const int MaxNameLength = 64;

    public static bool IsValidName(string? name)
    {
        if (name is null || name.Any(char.IsControl)) return false;
        for (var index = 0; index < name.Length; index++)
            if (char.IsSurrogate(name[index]) &&
                (!char.IsHighSurrogate(name[index]) || index + 1 >= name.Length || !char.IsLowSurrogate(name[++index])))
                return false;
        return name.Trim().EnumerateRunes().Count() is >= MinNameLength and <= MaxNameLength;
    }

    public static string NormalizeName(string name)
    {
        if (!IsValidName(name))
            throw new DomainValidationException("Имя сервисного аккаунта: от 3 до 64 символов без управляющих символов.");
        return name.Trim();
    }

    public static void RequireRole(ServiceAccountRole role)
    {
        if (role is not ServiceAccountRole.Lis and not ServiceAccountRole.Hl7Import)
            throw new DomainValidationException("Неизвестная роль сервисного аккаунта.");
    }

    public static bool IsSupportedHash(byte[]? hash) =>
        hash is { Length: LegacyHashLength } ||
        hash is { Length: >= MinEncodedHashLength and <= MaxEncodedHashLength } &&
        hash.AsSpan().StartsWith("$argon2id$v=19$"u8);

    public static void RequireHash(byte[]? hash)
    {
        if (!IsSupportedHash(hash))
            throw new DomainValidationException("Некорректный формат хеша API key.");
    }

    public static DateTimeOffset Timestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.UtcTicks % TimeSpan.TicksPerMicrosecond));
    }
}
