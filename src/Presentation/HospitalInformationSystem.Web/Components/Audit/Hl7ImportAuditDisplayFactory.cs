using System.Text.Json;
using HospitalInformationSystem.Application.Models.Hl7Import;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Web.Components.Audit;

public static class Hl7ImportAuditDisplayFactory
{
    public static Hl7ImportAuditDisplay? Create(AuditEventDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.EventType != AuditEventType.Hl7PatientImported) return null;
        var metadata = item.Metadata;
        var outcome = Text(metadata, "outcome", 64);
        var (label, style) = outcome switch
        {
            Hl7ImportAuditOutcomes.Success => ("Успех", "success"),
            Hl7ImportAuditOutcomes.Duplicate => ("Дубликат", "duplicate"),
            Hl7ImportAuditOutcomes.ValidationFailed or Hl7ImportAuditOutcomes.AuthenticationRejected or
                Hl7ImportAuditOutcomes.ProtocolRejected or Hl7ImportAuditOutcomes.InternalError => ("Ошибка", "error"),
            _ => ("Неизвестно", "unknown")
        };
        var isService = item.Actor.Kind == AuditActorKind.ServiceAccount &&
            item.Actor.ServiceAccountId is { } id && id != Guid.Empty;
        var fingerprint = Text(metadata, "clientCertificateSha256", 64);
        if (fingerprint is not { Length: 64 } || !fingerprint.All(Uri.IsHexDigit)) fingerprint = null;
        var notBefore = Timestamp(metadata, "clientCertificateNotBeforeUtc");
        var notAfter = Timestamp(metadata, "clientCertificateNotAfterUtc");
        if (notBefore > notAfter) (notBefore, notAfter) = (null, null);
        return new Hl7ImportAuditDisplay(label, style,
            isService ? SafeText(item.Actor.DisplayName, 256) : null,
            isService ? item.Actor.ServiceAccountId : null,
            SafeText(item.Actor.RequestId, 128) ?? Text(metadata, "correlationId", 128),
            Text(metadata, "sourceSystem", 64), Text(metadata, "externalPatientId", 128),
            Identifier(metadata, "targetId"), Count(metadata, "diagnosisCount"),
            fingerprint, Text(metadata, "clientCertificateSubject", 2048),
            Text(metadata, "clientCertificateIssuer", 2048), Text(metadata, "clientCertificateSerialNumber", 128),
            notBefore, notAfter);
    }

    private static string? Text(IReadOnlyDictionary<string, JsonElement>? metadata, string key, int maximum) =>
        metadata is not null && metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? SafeText(value.GetString(), maximum) : null;

    private static string? SafeText(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) ? null : value;

    private static Guid? Identifier(IReadOnlyDictionary<string, JsonElement>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetGuid(out var identifier) && identifier != Guid.Empty ? identifier : null;

    private static int? Count(IReadOnlyDictionary<string, JsonElement>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var count) && count is >= 0 and <= Hl7ImportProtocol.MaxDiagnoses ? count : null;

    private static DateTimeOffset? Timestamp(IReadOnlyDictionary<string, JsonElement>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var timestamp) && timestamp != default
            ? timestamp.ToUniversalTime() : null;
}
