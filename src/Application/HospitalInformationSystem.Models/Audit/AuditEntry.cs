using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Models.Audit;

public sealed record AuditEntry(
    Guid EventId,
    DateTimeOffset EventTimeUtc,
    AuditEventType EventType,
    AuditActor Actor,
    string Message,
    AuditMetadata Metadata);
