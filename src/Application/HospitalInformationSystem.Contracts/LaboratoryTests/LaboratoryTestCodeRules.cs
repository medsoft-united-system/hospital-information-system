using HospitalInformationSystem.Domain.LaboratoryTests;

namespace HospitalInformationSystem.Contracts.LaboratoryTests;

public static class LaboratoryTestCodeRules
{
    public const int MinCodeLength = LoincCodeValidation.MinCodeLength;
    public const int MaxCodeLength = LoincCodeValidation.MaxCodeLength;

    public static bool IsValidCode(string? code) => LoincCodeValidation.IsValidCode(code);
}
