using HospitalInformationSystem.Models.Audit;

namespace HospitalInformationSystem.Contracts.Audit;

public interface IAuditActorScope
{
    IDisposable Begin(AuditActor actor);
}
