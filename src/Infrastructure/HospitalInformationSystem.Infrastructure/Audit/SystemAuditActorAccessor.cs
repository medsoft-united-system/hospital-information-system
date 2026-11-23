using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class SystemAuditActorAccessor : IAuditActorAccessor
{
    public AuditActor GetCurrentActor() =>
        new(AuditActorKind.System, null, "HospitalInformationSystem", null, "HospitalInformationSystem");
}
