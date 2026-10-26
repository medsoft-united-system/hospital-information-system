using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Application.Audit;

public interface IAuditWriter
{
    void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null, AuditActor? actor = null);
}
