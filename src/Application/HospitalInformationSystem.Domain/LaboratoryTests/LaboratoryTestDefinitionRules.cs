using System.Text.RegularExpressions;

namespace HospitalInformationSystem.Domain.LaboratoryTests;

public static partial class LaboratoryTestDefinitionRules
{
    public const int MinCodeLength = 3;
    public const int MaxCodeLength = 32;
    public const int MinDisplayNameLength = 3;
    public const int MaxDisplayNameLength = 256;

    public static bool IsValidCode(string? code)
    {
        var value = code?.Trim();
        return value is not null && value.Length is >= MinCodeLength and <= MaxCodeLength &&
               LoincFormat().IsMatch(value);
    }

    public static bool IsValidDisplayName(string? name)
    {
        var value = name?.Trim();
        return value is not null && !value.Contains('\0') &&
               value.EnumerateRunes().Count() is >= MinDisplayNameLength and <= MaxDisplayNameLength;
    }

    [GeneratedRegex(@"\A[0-9]+-[0-9]\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LoincFormat();
}
