using HospitalInformationSystem.Abstractions.Repositories;
using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Models.Common;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Application.Audit.Services;

public sealed class AuditQueryService(IAuditRepository repository) : IAuditQueryService
{
    public Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || query.Page > int.MaxValue / query.PageSize)
            throw new DomainValidationException("Номер страницы должен быть положительным, размер — от 1 до 100.");
        if (query.EventType is { } eventType && !Enum.IsDefined(eventType))
            throw new DomainValidationException("Указан неизвестный тип события аудита.");
        if (query.From > query.To)
            throw new DomainValidationException("Начало периода не может быть позже окончания.");

        return repository.ListAsync(query, cancellationToken);
    }
}
