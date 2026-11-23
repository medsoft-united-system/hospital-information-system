using HospitalInformationSystem.Application.Models.Common;

namespace HospitalInformationSystem.Application.Common;

internal static class TextValidation
{
    public static void RejectNullCharacter(string? value, string label)
    {
        if (value?.Contains('\0') == true)
            throw new DomainValidationException($"{label}: нулевой символ недопустим.");
    }
}
