using HospitalInformationSystem.Contracts.Security;

namespace HospitalInformationSystem.Application.Audit;

public interface IAuditQueryService
{
    Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}
