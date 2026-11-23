using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Contracts.Audit;

public interface IAuditWriter
{
    void Append(AuditEventType eventType, string message, AuditMetadata? metadata = null, AuditActor? actor = null);
}
