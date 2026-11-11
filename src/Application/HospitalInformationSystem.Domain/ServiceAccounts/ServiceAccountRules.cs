using HospitalInformationSystem.Domain.Common;

namespace HospitalInformationSystem.Domain.ServiceAccounts;

public static class ServiceAccountRules
{
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

    public static void RequireHash(byte[]? hash)
    {
        if (hash is not { Length: 32 })
            throw new DomainValidationException("Хеш API key должен содержать 32 байта SHA-256.");
    }

    public static DateTimeOffset Timestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.UtcTicks % TimeSpan.TicksPerMicrosecond));
    }
}
