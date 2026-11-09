using System.Globalization;

namespace HospitalInformationSystem.Api.Fhir;

public static class FhirTimestamp
{
    public static DateTimeOffset ToUtcMilliseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    public static string Format(DateTimeOffset value) =>
        ToUtcMilliseconds(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
