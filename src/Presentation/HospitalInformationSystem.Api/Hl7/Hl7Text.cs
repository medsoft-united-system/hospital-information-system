using System.Text;

namespace HospitalInformationSystem.Api.Hl7;

internal static class Hl7Text
{
    public static string[] Split(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                if (index + 2 >= value.Length || value[index + 2] != '\\' ||
                    value[index + 1] is not ('F' or 'S' or 'R' or 'E' or 'T'))
                    throw new Hl7DecodeException(Hl7FailureKind.Protocol);
                index += 2;
            }
            else if (value[index] == separator)
            {
                parts.Add(value[start..index]);
                start = index + 1;
            }
        }
        parts.Add(value[start..]);
        return parts.ToArray();
    }

    public static string Atom(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c == '\\')
            {
                if (index + 2 >= value.Length || value[index + 2] != '\\')
                    throw new Hl7DecodeException(Hl7FailureKind.Protocol);
                result.Append(value[index + 1] switch
                {
                    'F' => '|', 'S' => '^', 'R' => '~', 'E' => '\\', 'T' => '&',
                    _ => throw new Hl7DecodeException(Hl7FailureKind.Protocol)
                });
                index += 2;
            }
            else
            {
                if (c is '|' or '^' or '~' or '&' || char.IsControl(c))
                    throw new Hl7DecodeException(Hl7FailureKind.Protocol);
                result.Append(c);
            }
        }
        return result.ToString();
    }

    public static string Escape(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
            result.Append(c switch
            {
                '|' => "\\F\\", '^' => "\\S\\", '~' => "\\R\\", '\\' => "\\E\\", '&' => "\\T\\",
                _ when char.IsControl(c) => throw new ArgumentException("HL7 text contains a control character."),
                _ => c.ToString()
            });
        return result.ToString();
    }

    public static string[] Components(string value, int count)
    {
        var parts = Split(value, '^');
        if (parts.Skip(count).Any(part => part.Length != 0))
            throw new Hl7DecodeException(Hl7FailureKind.Protocol);
        var result = new string[count];
        for (var index = 0; index < count; index++)
            result[index] = index < parts.Length ? Atom(parts[index]) : "";
        return result;
    }
}
