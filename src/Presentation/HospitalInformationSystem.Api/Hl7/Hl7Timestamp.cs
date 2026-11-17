using System.Globalization;

namespace HospitalInformationSystem.Api.Hl7;

internal static class Hl7Timestamp
{
    public static bool TryRead(string value, out DateTimeOffset result)
    {
        result = default;
        if (value.Length != 19 || value[14] is not ('+' or '-') ||
            !value[..14].All(char.IsAsciiDigit) || !value[15..].All(char.IsAsciiDigit) ||
            !DateTime.TryParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return false;
        var hours = int.Parse(value.AsSpan(15, 2), CultureInfo.InvariantCulture);
        var minutes = int.Parse(value.AsSpan(17, 2), CultureInfo.InvariantCulture);
        if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
            return false;
        var offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (value[14] == '-' ? -1 : 1));
        try
        {
            result = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
