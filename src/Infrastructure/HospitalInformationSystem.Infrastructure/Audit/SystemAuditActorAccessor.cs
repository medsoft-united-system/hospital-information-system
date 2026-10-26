using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class SystemAuditActorAccessor : IAuditActorAccessor
{
    public AuditActor GetCurrentActor() =>
        new(AuditActorKind.System, null, "HospitalInformationSystem", null, "HospitalInformationSystem");
}
