using HospitalInformationSystem.Application.Models.Audit;
namespace HospitalInformationSystem.Application.Contracts.Audit;

public interface IAuditActorAccessor
{
    AuditActor GetCurrentActor();
}
