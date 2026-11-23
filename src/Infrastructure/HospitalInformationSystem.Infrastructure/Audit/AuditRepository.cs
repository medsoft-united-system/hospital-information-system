using HospitalInformationSystem.Application.Abstractions.Repositories;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class AuditRepository(HospitalDbContext db) : IAuditRepository
{
    public void Add(AuditEntry entry) => db.AuditEvents.Add(AuditEntryMapping.ToDbo(entry));

    public async Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
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
