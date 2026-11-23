using HospitalInformationSystem.Models.Audit;
namespace HospitalInformationSystem.Contracts.Audit;

public interface IAuditActorAccessor
{
    AuditActor GetCurrentActor();
}
