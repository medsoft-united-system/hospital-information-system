using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Contracts.Audit;

public interface IAuditWriter
{
    void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null, AuditActor? actor = null);
}
