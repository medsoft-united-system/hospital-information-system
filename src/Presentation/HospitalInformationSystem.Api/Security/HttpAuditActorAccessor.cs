using System.Security.Claims;
using HospitalInformationSystem.Application.Contracts.Audit;
using HospitalInformationSystem.Application.Models.Audit;
using HospitalInformationSystem.Application.Models.Security;
using HospitalInformationSystem.Application.Models.ServiceAuth;
using HospitalInformationSystem.Infrastructure.Audit;

namespace HospitalInformationSystem.Api.Security;

public sealed class HttpAuditActorAccessor(IHttpContextAccessor contexts) : IAuditActorAccessor
{
    public AuditActor GetCurrentActor()
    {
        var context = contexts.HttpContext;
        if (context is null) return new SystemAuditActorAccessor().GetCurrentActor();
        var principal = context.User;
        var service = ServiceAccountPrincipal.TryGetIdentity(principal);
        if (service is not null)
            return new AuditActor(AuditActorKind.ServiceAccount, null,
                SafeText(service.Name, "Сервисный аккаунт LIS", 256),
                SafeText(context.TraceIdentifier, "request", 128), null, service.Id);
        var servicePrincipal = principal.Identities.Any(identity =>
                                   identity.AuthenticationType == ServiceAuthenticationDefaults.Scheme) ||
                               principal.HasClaim(ServiceAuthenticationDefaults.Claims.AccountKind,
                                   ServiceAuthenticationDefaults.AccountKind);
        if (!servicePrincipal && principal.Identity?.IsAuthenticated == true &&
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
