using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Application.Audit;

public sealed record AuditEntry(
    Guid EventId,
    DateTimeOffset EventTimeUtc,
    AuditEventType EventType,
    AuditActor Actor,
    string Message,
    AuditMetadata Metadata);
