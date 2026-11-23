using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Persistence;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Infrastructure.Contexts;
using Microsoft.EntityFrameworkCore;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class TrackedSecurityChanges(HospitalDbContext db, TimeProvider timeProvider,
    IAuditActorAccessor actorAccessor)
{
    public bool HasChanges()
    {
        db.ChangeTracker.DetectChanges();
        return db.ChangeTracker.Entries().Any(entry =>
            entry.Entity is MedicalWorkerDbo or PatientDbo or MedicalRecordHistoryEventDbo &&
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    public async Task AppendAsync(CancellationToken cancellationToken)
    {
        var factory = new AuditEventFactory(timeProvider);
        var actor = actorAccessor.GetCurrentActor();
        var workers = db.ChangeTracker.Entries<MedicalWorkerDbo>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified).ToArray();
        foreach (var entry in workers)
        {
            var worker = entry.Entity;
            var deleted = entry.State == EntityState.Modified && worker.IsDeleted &&
                          !entry.Property(x => x.IsDeleted).OriginalValue;
            var roleChanged = entry.State == EntityState.Modified &&
                              entry.Property(x => x.Role).OriginalValue != worker.Role;
            var eventType = entry.State == EntityState.Added ? AuditEventType.MedicalWorkerCreated :
                deleted ? AuditEventType.MedicalWorkerDeleted : AuditEventType.MedicalWorkerUpdated;
            var action = entry.State == EntityState.Added ? "create" : deleted ? "delete" :
                roleChanged ? "medical-role-changed" : "update";
            int? revokedSessions = null;
            if (deleted || roleChanged)
            {
                await db.MedicalWorkers.FromSqlInterpolated(
                        $"SELECT *, xmin FROM medical_workers WHERE id = {worker.Id} FOR UPDATE")
                    .SingleAsync(cancellationToken);
                revokedSessions = await RevokeWorkerAccessAsync(worker.Id, deleted, factory, actor, cancellationToken);
            }
            db.AuditEvents.Add(factory.Create(eventType,
                roleChanged ? "Изменена медицинская роль медработника." : "Изменены данные медработника.", actor,
                new AuditMetadata
                {
                    Action = action,
                    TargetId = worker.Id,
                    MedicalWorkerId = worker.Id,
                    RevokeReason = deleted ? "worker-deleted" : roleChanged ? "medical-role-changed" : null,
                    RevokedRefreshTokenCount = revokedSessions
                }));
        }

        foreach (var entry in db.ChangeTracker.Entries<PatientDbo>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToArray())
        {
            var (eventType, action) = entry.State switch
            {
                EntityState.Added => (AuditEventType.PatientCreated, "create"),
                EntityState.Deleted => (AuditEventType.PatientDeleted, "delete"),
                _ => (AuditEventType.PatientUpdated, "update")
            };
            db.AuditEvents.Add(factory.Create(eventType, "Изменены регистрационные данные пациента.", actor,
                new AuditMetadata { Action = action, TargetId = entry.Entity.Id }));
        }

        foreach (var entry in db.ChangeTracker.Entries<MedicalRecordHistoryEventDbo>()
                     .Where(entry => entry.State == EntityState.Added).ToArray())
        {
            var history = entry.Entity;
            var created = history.EventType == "MedicalRecordCreated";
            db.AuditEvents.Add(factory.Create(created ? AuditEventType.MedicalRecordCreated :
                    AuditEventType.MedicalRecordChanged, created ? "Создана ЭМК." : "Изменена ЭМК.", actor,
                new AuditMetadata
                {
                    Action = history.EventType,
                    TargetId = history.MedicalRecordId,
                    RecordEntryId = history.EntityId,
                    MedicalWorkerId = history.PerformedByMedicalWorkerId,
                    ServiceAccountId = history.PerformedByServiceAccountId
                }));
        }
    }

    private async Task<int> RevokeWorkerAccessAsync(Guid workerId, bool deleted, AuditEventFactory factory,
        AuditActor actor, CancellationToken cancellationToken)
    {
        var user = await db.Users.FromSqlInterpolated(
                $"SELECT * FROM \"AspNetUsers\" WHERE \"MedicalWorkerId\" = {workerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null) return 0;
        var now = timeProvider.GetUtcNow();
        var reason = deleted ? "worker-deleted" : "medical-role-changed";
        var sessions = await db.RefreshSessions.Where(session => session.UserId == user.Id &&
                session.RevokedAtUtc == null && session.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAtUtc = now;
            session.RevokeReason = reason;
        }
        if (deleted) user.IsActive = false;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        db.AuditEvents.Add(factory.Create(AuditEventType.AccountChanged,
            deleted ? "Аккаунт отключён после удаления медработника." : "Доступ отозван после смены медицинской роли.",
            actor, new AuditMetadata
            {
                Action = reason,
                TargetId = user.Id,
                AccountId = user.Id,
                MedicalWorkerId = workerId,
                RevokeReason = reason,
                RevokedRefreshTokenCount = sessions.Count
            }));
        return sessions.Count;
    }
}
