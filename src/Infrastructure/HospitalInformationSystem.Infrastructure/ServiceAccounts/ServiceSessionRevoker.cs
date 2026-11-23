using HospitalInformationSystem.Contracts.Abstractions;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Common;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.ServiceAccounts;

public sealed class ServiceSessionRevoker(HospitalDbContext db) : IServiceSessionRevoker
{
    public async Task<int> RevokeAllAsync(Guid serviceAccountId, DateTimeOffset atUtc, string reason,
        CancellationToken cancellationToken)
    {
        if (serviceAccountId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 128 ||
            reason.Any(char.IsControl))
            throw new DomainValidationException("Некорректные параметры отзыва сервисных сессий.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Отзыв сервисных сессий требует транзакции изменения аккаунта.");
        var at = atUtc.ToUniversalTime();
        var sessions = db.ServiceRefreshSessions.Where(session => session.ServiceAccountId == serviceAccountId &&
                                                                session.RevokedAtUtc == null);
        var count = await sessions.Where(session => session.ExpiresAtUtc > at).Select(session => session.FamilyId)
            .Distinct().CountAsync(cancellationToken);
        await sessions.ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc,
                session => session.CreatedAtUtc > at ? session.CreatedAtUtc : at)
            .SetProperty(session => session.RevokeReason, reason), cancellationToken);
        return count;
    }
}
