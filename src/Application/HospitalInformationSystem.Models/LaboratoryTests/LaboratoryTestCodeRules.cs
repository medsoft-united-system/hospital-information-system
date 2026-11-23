using HospitalInformationSystem.Models.LaboratoryTests;

namespace HospitalInformationSystem.Models.LaboratoryTests;

public static class LaboratoryTestCodeRules
{
    public const int MinCodeLength = LoincCodeValidation.MinCodeLength;
    public const int MaxCodeLength = LoincCodeValidation.MaxCodeLength;

    public static bool IsValidCode(string? code) => LoincCodeValidation.IsValidCode(code);
}
