using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class SystemAuditActorAccessor : IAuditActorAccessor
{
    public AuditActor GetCurrentActor() =>
        new(AuditActorKind.System, null, "HospitalInformationSystem", null, "HospitalInformationSystem");
}
