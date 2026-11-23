using HospitalInformationSystem.Application.Models.Audit;

namespace HospitalInformationSystem.Application.Contracts.Audit;

public interface IAuditActorScope
{
    IDisposable Begin(AuditActor actor);
}
