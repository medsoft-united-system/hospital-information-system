using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Abstractions.Repositories;

public interface IAuditRepository
{
    void Add(AuditEntry entry);
    Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}
