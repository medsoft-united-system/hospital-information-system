using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Models.Audit;

public sealed record AuditEntry(
    Guid EventId,
    DateTimeOffset EventTimeUtc,
    AuditEventType EventType,
    AuditActor Actor,
    string Message,
    AuditMetadata Metadata);
