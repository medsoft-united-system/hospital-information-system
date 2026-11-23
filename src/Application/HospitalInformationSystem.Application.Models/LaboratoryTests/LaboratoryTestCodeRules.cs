using HospitalInformationSystem.Application.Models.LaboratoryTests;

namespace HospitalInformationSystem.Application.Models.LaboratoryTests;

public static class LaboratoryTestCodeRules
{
    public const int MinCodeLength = LoincCodeValidation.MinCodeLength;
    public const int MaxCodeLength = LoincCodeValidation.MaxCodeLength;

    public static bool IsValidCode(string? code) => LoincCodeValidation.IsValidCode(code);
}
