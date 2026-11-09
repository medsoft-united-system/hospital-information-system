using System.Security.Cryptography;
using System.Text.Json;
using HospitalInformationSystem.Application.Fhir;
using HospitalInformationSystem.Domain.MedicalRecords;
using Microsoft.AspNetCore.DataProtection;

namespace HospitalInformationSystem.Api.Fhir;

public sealed class FhirCursorProtector(IDataProtectionProvider protection)
{
    private const int MaxCursorLength = 4096;
    private readonly IDataProtector _protector = protection.CreateProtector("MedSoft.Fhir.ReadCursor.v1");

    public string Protect(FhirSearchParameters search, FhirPagePosition position)
    {
        if (position.Id == Guid.Empty || position.AtUtc.UtcTicks % TimeSpan.TicksPerMillisecond != 0)
            throw new InvalidOperationException("Некорректная позиция страницы FHIR.");
        return _protector.Protect(JsonSerializer.Serialize(new CursorPayload(1, search.ResourceType, search.Status,
            search.BasedOn, search.Count, position.AtUtc.UtcTicks, position.Id)));
    }

    public FhirPagePosition Unprotect(string cursor, string resourceType, LaboratoryAnalysisRequestStatus? status,
        Guid? basedOn, int count)
    {
        if (cursor.Length is < 1 or > MaxCursorLength ||
            cursor.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw InvalidCursor();
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(_protector.Unprotect(cursor));
            if (payload is null || payload.Version != 1 || payload.ResourceType != resourceType ||
                payload.Status != status || payload.BasedOn != basedOn || payload.Count != count ||
                payload.Id == Guid.Empty || payload.AtUtcTicks < DateTimeOffset.MinValue.UtcTicks ||
                payload.AtUtcTicks > DateTimeOffset.MaxValue.UtcTicks ||
                payload.AtUtcTicks % TimeSpan.TicksPerMillisecond != 0)
                throw InvalidCursor();
            return new FhirPagePosition(new DateTimeOffset(payload.AtUtcTicks, TimeSpan.Zero), payload.Id);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw InvalidCursor();
        }
    }

    private static FhirHttpException InvalidCursor() =>
        FhirSearchParser.InvalidSearch("Маркер страницы FHIR недействителен или относится к другому поиску.");

    private sealed record CursorPayload(int Version, string ResourceType, LaboratoryAnalysisRequestStatus? Status,
        Guid? BasedOn, int Count, long AtUtcTicks, Guid Id);
}
