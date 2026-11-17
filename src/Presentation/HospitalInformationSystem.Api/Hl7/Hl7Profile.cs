namespace HospitalInformationSystem.Api.Hl7;

internal static class Hl7Profile
{
    public static DateTimeOffset ReadTime(string value, Hl7FailureKind kind) =>
        Hl7Timestamp.TryRead(value, out var time) ? time : throw new Hl7DecodeException(kind);

    public static string Required(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new Hl7DecodeException(Hl7FailureKind.Protocol);
        return value;
    }

    public static string Field(string[] segment, int index) => index < segment.Length ? segment[index] : "";

    public static void OnlyFields(string[] segment, params int[] allowed)
    {
        for (var index = 1; index < segment.Length; index++)
            if (segment[index].Length != 0 && !allowed.Contains(index))
                throw new Hl7DecodeException(Hl7FailureKind.Protocol);
    }

    public static void Require(bool condition)
    {
        if (!condition) throw new Hl7DecodeException(Hl7FailureKind.Protocol);
    }
}
