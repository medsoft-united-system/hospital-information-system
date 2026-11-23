using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;

namespace HospitalInformationSystem.Application.Abstractions.Repositories;

public interface IAuditRepository
{
    void Add(AuditEntry entry);
    Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}
