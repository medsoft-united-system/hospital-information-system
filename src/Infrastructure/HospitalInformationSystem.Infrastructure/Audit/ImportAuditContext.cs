using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;

namespace HospitalInformationSystem.Infrastructure.Audit;

public sealed class ImportAuditContext : IAuditActorAccessor, IAuditActorScope
{
    private AuditActor? _actor;
    public bool IsActive => _actor is not null;

    public IDisposable Begin(AuditActor actor)
    {
        if (_actor is not null) throw new InvalidOperationException("Контекст импорта уже используется.");
        _actor = actor;
        return new Scope(this);
    }

    public AuditActor GetCurrentActor() =>
        _actor ?? throw new InvalidOperationException("Контекст импорта не задан.");

    private sealed class Scope(ImportAuditContext context) : IDisposable
    {
        public void Dispose() => context._actor = null;
    }
}
