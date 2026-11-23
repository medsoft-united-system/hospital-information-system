using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Audit.Factories;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Audit.Writers;

public sealed class AuditWriter(IAuditRepository repository, AuditEntryFactory factory, IAuditActorAccessor actors)
    : IAuditWriter
{
    public void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null,
        AuditActor? actor = null) =>
        repository.Add(factory.Create(eventType, message, actor ?? actors.GetCurrentActor(), metadata));
}
