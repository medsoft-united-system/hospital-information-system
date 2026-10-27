using System.Security.Claims;
using HospitalInformationSystem.Application.Audit;
using HospitalInformationSystem.Contracts.Security;
using HospitalInformationSystem.Infrastructure.Audit;

namespace HospitalInformationSystem.Api.Security;

public sealed class HttpAuditActorAccessor(IHttpContextAccessor contexts) : IAuditActorAccessor
{
    public AuditActor GetCurrentActor()
    {
        var context = contexts.HttpContext;
        if (context is null) return new SystemAuditActorAccessor().GetCurrentActor();
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true &&
            Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"),
                out var userId) && userId != Guid.Empty)
            return new AuditActor(AuditActorKind.User, userId,
                SafeText(principal.Identity.Name, "Пользователь", 256), SafeText(context.TraceIdentifier, "request", 128),
                null);
        return new AuditActor(AuditActorKind.Anonymous, null, "Анонимный пользователь",
            SafeText(context.TraceIdentifier, "request", 128), null);
    }

    private static string SafeText(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = new string(value.Where(character => !char.IsControl(character)).Take(maxLength).ToArray());
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }
}
