using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalInformationSystem.Application.Models.Security;

public sealed record AuditEventDto(
    Guid EventId,
    DateTimeOffset EventTime,
    AuditEventType EventType,
    AuditActorDto Actor,
    string Message,
    IReadOnlyDictionary<string, JsonElement> Metadata);
