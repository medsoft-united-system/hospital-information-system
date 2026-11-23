namespace HospitalInformationSystem.Models.Auth;

public static class BrowserTabSession
{
    public const string HeaderName = "X-MedSoft-Tab-Id";

    public static bool IsValid(string? value) => value is { Length: 32 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
