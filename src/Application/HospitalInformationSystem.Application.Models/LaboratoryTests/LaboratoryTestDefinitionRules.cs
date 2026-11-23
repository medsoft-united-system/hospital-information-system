namespace HospitalInformationSystem.Application.Models.LaboratoryTests;

public static class LaboratoryTestDefinitionRules
{
    public const int MinCodeLength = LoincCodeValidation.MinCodeLength;
    public const int MaxCodeLength = LoincCodeValidation.MaxCodeLength;
    public const int MinDisplayNameLength = 3;
    public const int MaxDisplayNameLength = 256;

    public static bool IsValidCode(string? code) => LoincCodeValidation.IsValidCode(code);

    public static bool IsWellFormedCode(string? code) => LoincCodeValidation.IsWellFormedCode(code);

    public static bool IsValidDisplayName(string? name)
    {
        var value = name?.Trim();
        return value is not null && !value.Contains('\0') &&
               value.EnumerateRunes().Count() is >= MinDisplayNameLength and <= MaxDisplayNameLength;
    }
}
