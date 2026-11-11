using System.Globalization;
using HospitalInformationSystem.Domain.MedicalRecords;

namespace HospitalInformationSystem.Api.Fhir;

public sealed class FhirSearchParser(FhirCursorProtector cursors)
{
    public FhirSearchParameters ParseServiceRequests(IQueryCollection query)
    {
        ValidateKeys(query, "status", "_count", "_cursor");
        var statusText = Single(query, "status");
        LaboratoryAnalysisRequestStatus? status = statusText switch
        {
            null => null,
            "active" => LaboratoryAnalysisRequestStatus.Active,
            "completed" => LaboratoryAnalysisRequestStatus.Completed,
            _ => throw InvalidSearch("Параметр status должен быть active или completed.")
        };
        var count = Count(query);
        var cursor = Single(query, "_cursor");
        var after = cursor is null ? null : cursors.Unprotect(cursor, "ServiceRequest", status, null, count);
        return new FhirSearchParameters("ServiceRequest", status, null, count, after, cursor);
    }

    public FhirSearchParameters ParseObservations(IQueryCollection query)
    {
        ValidateKeys(query, "based-on", "_count", "_cursor");
        var basedOnText = Single(query, "based-on");
        const string prefix = "ServiceRequest/";
        if (basedOnText is null || !basedOnText.StartsWith(prefix, StringComparison.Ordinal) ||
            !TryResourceId(basedOnText[prefix.Length..], out var basedOn))
            throw InvalidSearch("Укажите based-on в формате ServiceRequest/UUID.");
        var count = Count(query);
        var cursor = Single(query, "_cursor");
        var after = cursor is null ? null : cursors.Unprotect(cursor, "Observation", null, basedOn, count);
        return new FhirSearchParameters("Observation", null, basedOn, count, after, cursor);
    }

    public static Guid ParseResourceId(string value) => TryResourceId(value, out var id)
        ? id : throw new FhirHttpException(StatusCodes.Status400BadRequest, "invalid_resource",
            "Некорректный идентификатор ресурса FHIR.");

    public static void RejectQuery(IQueryCollection query)
    {
        if (query.Count != 0)
            throw InvalidSearch("Эта операция FHIR не поддерживает параметры запроса.");
    }

    private static bool TryResourceId(string? value, out Guid id)
    {
        id = Guid.Empty;
        return value is { Length: 36 } && Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;
    }

    private static void ValidateKeys(IQueryCollection query, params string[] allowed)
    {
        foreach (var parameter in query)
            if (!allowed.Contains(parameter.Key, StringComparer.Ordinal) || parameter.Value.Count != 1 ||
                string.IsNullOrEmpty(parameter.Value[0]))
                throw InvalidSearch("Параметры поиска FHIR должны быть поддерживаемыми, непустыми и без повторов.");
    }

    private static string? Single(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) ? values[0] : null;

    private static int Count(IQueryCollection query)
    {
        var text = Single(query, "_count");
        if (text is null) return 20;
        if (text.Length == 0 || text.Any(character => character is < '0' or > '9') ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 100)
            throw InvalidSearch("Параметр _count должен быть целым числом от 1 до 100.");
        return count;
    }

    internal static FhirHttpException InvalidSearch(string message) =>
        new(StatusCodes.Status400BadRequest, "invalid_search", message);
}
