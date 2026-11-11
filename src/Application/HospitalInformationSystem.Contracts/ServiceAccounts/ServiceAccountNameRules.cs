namespace HospitalInformationSystem.Contracts.ServiceAccounts;

public static class ServiceAccountNameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    public static bool IsValid(string? name)
    {
        if (name is null || name.Any(char.IsControl)) return false;
        for (var index = 0; index < name.Length; index++)
            if (char.IsSurrogate(name[index]) &&
                (!char.IsHighSurrogate(name[index]) || index + 1 >= name.Length || !char.IsLowSurrogate(name[++index])))
                return false;
        return name.Trim().EnumerateRunes().Count() is >= MinLength and <= MaxLength;
    }
}
