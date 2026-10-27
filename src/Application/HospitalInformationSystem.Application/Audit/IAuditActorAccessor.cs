namespace HospitalInformationSystem.Application.Audit;

public interface IAuditActorAccessor
{
    AuditActor GetCurrentActor();
}
