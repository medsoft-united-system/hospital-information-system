using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Application.Audit.Services;

public sealed class AuditWriter(IAuditRepository repository, AuditEntryFactory factory, IAuditActorAccessor actors)
    : IAuditWriter
{
    public void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null,
        AuditActor? actor = null) =>
        repository.Add(factory.Create(eventType, message, actor ?? actors.GetCurrentActor(), metadata));
}
