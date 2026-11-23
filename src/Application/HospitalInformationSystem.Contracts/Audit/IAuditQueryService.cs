using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Contracts.Audit;

public interface IAuditQueryService
{
    Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}
