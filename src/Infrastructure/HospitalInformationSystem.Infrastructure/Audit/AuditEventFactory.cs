using HospitalInformationSystem.Application.Audit.Services;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Persistence;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class AuditEventFactory(TimeProvider timeProvider)
{
    private readonly AuditEntryFactory _entries = new(timeProvider);

    public AuditEventDbo Create(AuditEventType eventType, string message, AuditActor actor,
        AuditMetadata? metadata = null) =>
        AuditEntryMapping.ToDbo(_entries.Create(eventType, message, actor, metadata));
}
