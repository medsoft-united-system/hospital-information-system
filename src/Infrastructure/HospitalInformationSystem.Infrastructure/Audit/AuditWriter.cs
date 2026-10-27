using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Contexts;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class AuditWriter(HospitalDbContext db, AuditEventFactory factory, IAuditActorAccessor actors)
    : IAuditWriter
{
    public void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null,
        AuditActor? actor = null) =>
        db.AuditEvents.Add(factory.Create(eventType, message, actor ?? actors.GetCurrentActor(), metadata));
}
