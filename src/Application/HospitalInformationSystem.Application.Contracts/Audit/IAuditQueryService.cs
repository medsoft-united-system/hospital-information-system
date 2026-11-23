using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Contracts.Audit;

public interface IAuditQueryService
{
    Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}
