namespace HospitalInformationSystem.Models.LaboratoryTests;

internal static class LoincCodeValidation
{
    public const int MinCodeLength = 3;
    public const int MaxCodeLength = 32;

    public static bool IsValidCode(string? code)
    {
        if (!IsWellFormedCode(code)) return false;
        var value = code!.Trim();
        var sum = value[^1] - '0';
        var doubleDigit = true;
        for (var index = value.Length - 3; index >= 0; index--)
        {
            var digit = value[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0 || IsHistoricalException(value);
    }

    public static bool IsWellFormedCode(string? code)
    {
        var value = code?.Trim();
        if (value is null || value.Length is < MinCodeLength or > MaxCodeLength || value[^2] != '-' ||
            value[^1] is < '0' or > '9')
            return false;

        for (var index = 0; index < value.Length - 2; index++)
            if (value[index] is < '0' or > '9') return false;
        return true;
    }

    private static bool IsHistoricalException(string code) => code is
        "11491-6" or "11501-6" or "5895-7" or "5896-5" or "5897-3" or "5928-1" or "6028-3" or
        "6415-5" or "6580-7" or "6645-7" or "6646-5" or "6647-3" or "6648-1" or "6649-9";
}
