using HospitalInformationSystem.Contracts.Audit;
using HospitalInformationSystem.Infrastructure.Contexts;
using HospitalInformationSystem.Models.Audit;
using HospitalInformationSystem.Models.Security;

namespace HospitalInformationSystem.Api.Security;

public sealed class AuthenticationAuditService(IAuditWriter audit, HospitalDbContext db, IHttpContextAccessor contexts)
{
    public static bool IsLoginPath(PathString path) => path.Value?.TrimEnd('/') is { } value &&
        (value.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("/api/auth/admin/login", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("/api/auth/admin/confirm", StringComparison.OrdinalIgnoreCase));

    public async Task LoginFailedAsync(string? attemptedUserName, CancellationToken cancellationToken)
    {
        audit.Append(AuditEventType.LoginFailed, "Вход отклонён.",
            new AuditMetadata
            {
                Action = "login", AttemptedUserName = Sanitize(attemptedUserName, 128),
                RemoteIp = RemoteIp(), Outcome = "invalid-credentials"
            }, AnonymousActor());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task LoginRateLimitedAsync(CancellationToken cancellationToken)
    {
        audit.Append(AuditEventType.LoginFailed, "Вход отклонён из-за ограничения числа запросов.",
            new AuditMetadata { Action = "login", RemoteIp = RemoteIp(), Outcome = "rate-limited" }, AnonymousActor());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task LoginCsrfRejectedAsync(CancellationToken cancellationToken)
    {
        audit.Append(AuditEventType.LoginFailed, "Вход отклонён из-за ошибки проверки CSRF.",
            new AuditMetadata { Action = "login", RemoteIp = RemoteIp(), Outcome = "csrf-rejected" }, AnonymousActor());
        await db.SaveChangesAsync(cancellationToken);
    }

    private AuditActor AnonymousActor() => new(AuditActorKind.Anonymous, null, "Анонимный пользователь", RequestId(), null);

    private string RequestId() => Sanitize(contexts.HttpContext?.TraceIdentifier, 128) ?? Guid.NewGuid().ToString("N");

    private string? RemoteIp() => contexts.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = new string(value.Where(character => !char.IsControl(character)).Take(maxLength).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}
