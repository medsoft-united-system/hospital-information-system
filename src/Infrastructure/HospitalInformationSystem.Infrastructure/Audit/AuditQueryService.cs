using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Domain.Common;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class AuditQueryService(HospitalDbContext db) : IAuditQueryService
{
    public async Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || query.Page > int.MaxValue / query.PageSize)
            throw new DomainValidationException("Номер страницы должен быть положительным, размер — от 1 до 100.");
        if (query.EventType is { } eventType && !Enum.IsDefined(eventType))
            throw new DomainValidationException("Указан неизвестный тип события аудита.");
        if (query.From > query.To)
            throw new DomainValidationException("Начало периода не может быть позже окончания.");

        var events = db.AuditEvents.AsNoTracking();
        if (query.EventType is { } type)
        {
            var name = type.ToString();
            events = type switch
            {
                AuditEventType.ServiceLogin => events.Where(entry => entry.EventType == name ||
                    entry.EventType == nameof(AuditEventType.UserLogin) && entry.ActorKind == "service"),
                AuditEventType.UserLogin => events.Where(entry => entry.EventType == name && entry.ActorKind != "service"),
                _ => events.Where(entry => entry.EventType == name)
            };
        }
        if (query.From is { } from)
        {
            var instant = from.ToUniversalTime();
            events = events.Where(entry => entry.EventTimeUtc >= instant);
        }
        if (query.To is { } to)
        {
            var instant = to.ToUniversalTime();
            events = events.Where(entry => entry.EventTimeUtc <= instant);
        }

        var page = await events.OrderByDescending(entry => entry.EventTimeUtc)
            .ThenByDescending(entry => entry.EventId)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize + 1)
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditEventDto>(page.Take(query.PageSize).Select(AuditEntryMapping.ToDto).ToArray(),
            query.Page, query.PageSize, page.Count > query.PageSize);
    }
}
